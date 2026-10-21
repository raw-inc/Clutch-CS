using System.Net;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>クラスタ観測の一時失敗・復帰・取消・重複取得を確認します。</summary>
public sealed class EditorClusterObservationTests
{
    /// <summary>通信またはJSONの一時失敗では前回値を残し、成功復帰したら警告を消します。</summary>
    /// <returns>警告と観測集合が同じ取得結果を表す確認です。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("")]
    public async Task FailureRetainsLastObservationAndSuccessClearsWarningAsync(string? invalidBody)
    {
        int calls = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            calls++;
            if (calls == 2)
            {
                return Task.FromResult(invalidBody is null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                                                           : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(invalidBody) });
            }
            return Task.FromResult(Response(calls.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorClusterObservations observed = new(new(client));
        await observed.RefreshAsync().ConfigureAwait(true);
        Assert.Equal("1", Assert.Single(observed.Clusters).ClusterId);
        Assert.False(observed.Unavailable);
        await observed.RefreshAsync().ConfigureAwait(true);
        Assert.True(observed.Unavailable);
        Assert.Equal("1", Assert.Single(observed.Clusters).ClusterId);
        await observed.RefreshAsync().ConfigureAwait(true);
        Assert.False(observed.Unavailable);
        Assert.Equal("3", Assert.Single(observed.Clusters).ClusterId);
    }

    /// <summary>取得中の手動再要求を重ねず、取消された古い要求で観測を上書きしません。</summary>
    /// <returns>同時一件と終了取消の扱いです。</returns>
    [Fact]
    public async Task OverlappingRefreshAndOwnerCancellationDoNotCreateStaleWarningAsync()
    {
        int calls = 0;
        TaskCompletionSource<HttpResponseMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new((_, token) =>
        {
            Interlocked.Increment(ref calls);
            return reply.Task.WaitAsync(token);
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorClusterObservations observed = new(new(client));
        using CancellationTokenSource lifetime = new();
        Task pending = observed.RefreshAsync(lifetime.Token);
        await observed.RefreshAsync(lifetime.Token).ConfigureAwait(true);
        Assert.True(observed.Refreshing);
        Assert.Equal(1, calls);
        await lifetime.CancelAsync().ConfigureAwait(true);
        await pending.ConfigureAwait(true);
        Assert.False(observed.Refreshing);
        Assert.False(observed.Unavailable);
        Assert.Empty(observed.Clusters);
        using HttpResponseMessage recovered = Response("recovered");
        reply.SetResult(recovered);
        await observed.RefreshAsync().ConfigureAwait(true);
        Assert.Equal(2, calls);
        Assert.Equal("recovered", Assert.Single(observed.Clusters).ClusterId);
    }

    /// <summary>ClientHostが返す実際の契約をHTTP境界で返します。</summary>
    /// <returns>一件のクラスタ観測です。</returns>
    private static HttpResponseMessage Response(string id)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new DiscoveredCluster(id, [], [], DateTimeOffset.UtcNow) }, options: ContractJson.Options) };
}
