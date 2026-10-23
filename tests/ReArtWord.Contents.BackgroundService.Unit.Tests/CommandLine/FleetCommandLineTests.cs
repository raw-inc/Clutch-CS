using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.CommandLine;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.CommandLine;

/// <summary>HTTPサーバーを使い、Fleet UpdateのCLIコマンドと終了コードを検証します。</summary>
public sealed class FleetCommandLineTests
{
    /// <summary>Coreを個別に指定せずに計画を作成し、同じ操作IDで開始・照会できることを確認します。</summary>
    /// <returns>CLIの送信内容と終了コードの検証。</returns>
    [Fact]
    public async Task PreviewStartQueryAndReconcileKeepTheOriginalOperation()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid operation = Guid.NewGuid();
        int starts = 0;
        object plan = new { operationId = operation, release = new { maximumParallelism = 1 }, targets = new[] { new { clusterId = "studio", peer = new { peerId = "screen-a" } } } };
        app.MapPost("/client/updates/preview", () => plan);
        app.MapPost("/client/updates", async (HttpContext context) =>
        {
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal(operation, body.GetProperty("operationId").GetGuid());
            Assert.Equal(1, body.GetProperty("release").GetProperty("maximumParallelism").GetInt32());
            Interlocked.Increment(ref starts);
            return new { request = plan, completed = false, outcome = "unknown" };
        });
        app.MapGet("/client/updates/{id:guid}", (Guid id) => new { request = new { operationId = id }, completed = true, outcome = "succeeded" });
        app.MapPost("/client/updates/{id:guid}/reconcile", (Guid id) => new { request = new { operationId = id }, completed = true, outcome = "unknown" });
        app.MapGet("/client/updates", () => new[] { new { operationId = operation } });
        await app.StartAsync().ConfigureAwait(true);
        string url = app.Urls.Single();
        (int previewCode, string output) = await RunAsync(url, ["fleet", "preview", "--body", "-"], "{}").ConfigureAwait(true);
        Assert.Equal(0, previewCode);
        using JsonDocument preview = JsonDocument.Parse(output);
        string body = preview.RootElement.GetProperty("response").GetRawText();
        Assert.Equal(5, (await RunAsync(url, ["fleet", "start", "--body", "-"], body).ConfigureAwait(true)).Code);
        Assert.Equal(0, (await RunAsync(url, ["fleet", "status", "--id", operation.ToString("D")]).ConfigureAwait(true)).Code);
        Assert.Equal(5, (await RunAsync(url, ["fleet", "reconcile", "--id", operation.ToString("D")]).ConfigureAwait(true)).Code);
        Assert.Equal(0, (await RunAsync(url, ["fleet", "list"]).ConfigureAwait(true)).Code);
        Assert.Equal(1, starts);
    }

    /// <summary>不正な計画や未完了の応答に対して、終了コード0を返さないことを確認します。</summary>
    [Theory]
    [InlineData("fleet preview", "{\"operationId\":12,\"targets\":[],\"release\":{}}", 5)]
    [InlineData("fleet start", "{\"completed\":false,\"outcome\":\"succeeded\"}", 5)]
    [InlineData("fleet status", "{\"completed\":true,\"outcome\":\"failed\"}", 4)]
    public void IncompleteResultsRemainNonzero(string command, string json, int expected)
    {
        using JsonDocument response = JsonDocument.Parse(json);
        Assert.Equal(expected, CliResponse.Classify(command, response.RootElement, 200));
    }

    /// <summary>ローカルのHTTPサーバーに対してCLIを実行します。</summary>
    /// <returns>終了コードと標準出力。</returns>
    private static async Task<(int Code, string Output)> RunAsync(string url, string[] command, string body = "", CancellationToken cancellationToken = default)
    {
        using StringReader input = new(body);
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using StringWriter error = new(CultureInfo.InvariantCulture);
        int code = await CommandLineApplication.RunAsync(["--url", url, .. command], input, output, error, cancellationToken).ConfigureAwait(true);
        return (code, output.ToString());
    }
}
