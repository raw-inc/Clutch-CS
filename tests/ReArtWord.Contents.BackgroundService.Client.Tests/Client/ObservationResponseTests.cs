using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実際の応答ヘッダー生成と読取りで、日時補正と再取得・遅着を区別します。</summary>
public sealed class ObservationResponseTests
{
    /// <summary>HTTPミドルウェアと通常のManagementApiを通し、時計を戻したHostの新しい応答を採用します。</summary>
    /// <returns>ブラウザー用の要求照合と応答順序の実通信による確認です。</returns>
    [Fact]
    public async Task ManagementApiFollowsAClockCorrectionThroughActualHttpAsync()
    {
        ObservationClock source = new(), browser = new();
        ServerClock receiver = new(browser);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        ClientHttpPolicy.Configure(app, source);
        app.MapGet("/api/clock-probe", () => Results.Json(new { value = true }));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await app.StartAsync(timeout.Token).ConfigureAwait(true);
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };
            ManagementApi api = new(client, receiver);
            Assert.True((await api.GetAsync<JsonElement>("/api/clock-probe", timeout.Token).ConfigureAwait(true)).GetProperty("value").GetBoolean());
            Assert.Equal(source.GetUtcNow(), api.UtcNow);
            source.Adjust(TimeSpan.FromHours(-1));
            _ = await api.GetAsync<JsonElement>("/api/clock-probe", timeout.Token).ConfigureAwait(true);
            Assert.Equal(source.GetUtcNow(), api.UtcNow);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>同じHostの新しい応答なら時計補正を反映し、古い応答や別要求のキャッシュは採用しません。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void SequenceAndRequestIdentityDistinguishClockAdjustmentFromCachedResponses(int correction)
    {
        ObservationClock source = new(), browser = new();
        ClientResponseClock host = new(source);
        ServerClock receiver = new(browser);
        Guid firstId = Guid.NewGuid(), nextId = Guid.NewGuid();
        using HttpResponseMessage first = Response(host, firstId);
        receiver.Observe(first.Headers, receiver.Timestamp, firstId);
        browser.Advance(TimeSpan.FromSeconds(2));
        source.Advance(TimeSpan.FromSeconds(2));
        source.Adjust(TimeSpan.FromSeconds(correction));
        using HttpResponseMessage next = Response(host, nextId);
        receiver.Observe(next.Headers, receiver.Timestamp, nextId);
        Assert.Equal(source.GetUtcNow(), receiver.UtcNow);
        DateTimeOffset accepted = receiver.UtcNow;
        browser.Advance(TimeSpan.FromSeconds(10));

        // 前の要求の応答を新しいGETのキャッシュとして返されても、時計を更新しません。
        receiver.Observe(first.Headers, receiver.Timestamp, Guid.NewGuid());
        receiver.Observe(next.Headers, receiver.Timestamp, nextId);
        receiver.Observe(first.Headers, 0, firstId);
        Assert.Equal(accepted.AddSeconds(10), receiver.UtcNow);

        // 一度連番付き応答を確認した同じHostは、情報の欠けた応答へ降格させません。
        using HttpResponseMessage incomplete = Response(host, Guid.NewGuid());
        incomplete.Headers.Remove(ObservationHeaders.Sequence);
        receiver.Observe(incomplete.Headers, receiver.Timestamp);
        Assert.Equal(accepted.AddSeconds(10), receiver.UtcNow);
    }

    /// <summary>再起動後のHostを採用してから、前のHostへ送った要求の遅着応答には戻りません。</summary>
    [Fact]
    public void NewHostCanResetItsClockWithoutAcceptingALateReplyFromTheOldHost()
    {
        ObservationClock source = new(), browser = new();
        ClientResponseClock firstHost = new(source);
        ServerClock receiver = new(browser);
        Guid initialId = Guid.NewGuid(), lateId = Guid.NewGuid(), restartId = Guid.NewGuid();
        using HttpResponseMessage initial = Response(firstHost, initialId);
        receiver.Observe(initial.Headers, 0, initialId, receiver.NextRequestOrder());
        long lateStarted = receiver.Timestamp;
        long lateOrder = receiver.NextRequestOrder();
        using HttpResponseMessage late = Response(firstHost, lateId);
        source.Adjust(TimeSpan.FromHours(-3));
        ClientResponseClock restartedHost = new(source);
        using HttpResponseMessage restarted = Response(restartedHost, restartId);
        receiver.Observe(restarted.Headers, receiver.Timestamp, restartId, receiver.NextRequestOrder());
        Assert.Equal(source.GetUtcNow(), receiver.UtcNow);
        receiver.Observe(late.Headers, lateStarted, lateId, lateOrder);
        Assert.Equal(source.GetUtcNow(), receiver.UtcNow);
    }

    /// <summary>新方式の照合情報が不正な場合は、日時だけの判定へ戻しません。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    public void MalformedSequenceCannotReplaceTheObservationClock(string sequence)
    {
        ObservationClock source = new(), browser = new();
        source.Adjust(TimeSpan.FromHours(2));
        ClientResponseClock host = new(source);
        ServerClock receiver = new(browser);
        Guid request = Guid.NewGuid();
        using HttpResponseMessage response = Response(host, request);
        response.Headers.Remove(ObservationHeaders.Sequence);
        response.Headers.Add(ObservationHeaders.Sequence, sequence);
        receiver.Observe(response.Headers, 0, request);
        Assert.Equal(browser.GetUtcNow(), receiver.UtcNow);
    }

    /// <summary>Hostが書くヘッダーをHTTP応答の形式へ移します。</summary>
    /// <returns>呼出元で破棄する応答です。</returns>
    private static HttpResponseMessage Response(ClientResponseClock clock, Guid request)
    {
        DefaultHttpContext context = new();
        context.Request.Headers[ObservationHeaders.Request] = request.ToString("D");
        clock.Write(context);
        HttpResponseMessage response = new(HttpStatusCode.OK);
        foreach ((string name, Microsoft.Extensions.Primitives.StringValues values) in context.Response.Headers)
        {
            response.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        return response;
    }
}
