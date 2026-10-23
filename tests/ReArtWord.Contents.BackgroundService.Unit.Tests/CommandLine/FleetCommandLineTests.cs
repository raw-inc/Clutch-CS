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
    /// <summary>アーカイブとFleetの引数制約を保ち、不適切な指定では管理要求を送らないことを確認します。</summary>
    /// <returns>入力エラーと、HTTP要求が送られていないことの検証。</returns>
    [Theory]
    [InlineData("archive", "export", "--body", "-")]
    [InlineData("archive", "preview", "--body", "-")]
    [InlineData("archive", "import", "--body", "-")]
    [InlineData("archive", "reconcile", "--body", "-")]
    [InlineData("archive", "reconcile", "--operation-id", "11111111-1111-4111-8111-111111111111")]
    [InlineData("fleet", "reconcile", "--body", "-")]
    [InlineData("fleet", "reconcile", "--operation-id", "11111111-1111-4111-8111-111111111111")]
    [InlineData("fleet", "preview", "--cluster", "studio")]
    public async Task CommandOptionsAreRejectedBeforeSendingRequests(string family, string action, string option, string value)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        int requests = 0;
        app.MapFallback(() =>
        {
            Interlocked.Increment(ref requests);
            return Results.BadRequest();
        });
        await app.StartAsync().ConfigureAwait(true);
        List<string> arguments = [family, action];
        string archive = "archive";
        if (string.Equals(family, archive, StringComparison.Ordinal))
        {
            arguments.AddRange(["--cluster", "studio", "--peer", "screen-a"]);
            switch (action)
            {
                case "export" or "preview": arguments.AddRange(["--file", "archive.zip"]); break;
                case "import": arguments.AddRange(["--confirmation", "confirmed-preview"]); break;
            }
        }
        else
        {
            string preview = "preview";
            if (string.Equals(action, preview, StringComparison.Ordinal))
            {
                arguments.AddRange(["--body", "-"]);
            }
        }
        string import = "import", reconcile = "reconcile";
        if (string.Equals(action, import, StringComparison.Ordinal) || string.Equals(action, reconcile, StringComparison.Ordinal))
        {
            arguments.AddRange(["--id", Guid.NewGuid().ToString("D")]);
        }
        arguments.AddRange([option, value]);
        (int code, _) = await RunAsync(app.Urls.Single(), arguments.ToArray(), "{}").ConfigureAwait(true);
        Assert.Equal(2, code);
        Assert.Equal(0, requests);
    }

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
