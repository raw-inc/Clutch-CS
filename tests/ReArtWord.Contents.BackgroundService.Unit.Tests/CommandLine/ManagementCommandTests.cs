using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.CommandLine;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.CommandLine;

/// <summary>共通設定と診断の公開HTTP契約を確認します。</summary>
public sealed class ManagementCommandTests
{
    /// <summary>軽量読取と元ID照会を公開経路へ配送します。</summary>
    /// <returns>実HTTP応答の分類です。</returns>
    [Theory]
    [InlineData("configuration get", "/api/configuration", "{\"saved\":null,\"effective\":null}")]
    [InlineData("resources get", "/api/resources", "{\"capturedAt\":\"2026-09-13T00:00:00Z\"}")]
    [InlineData("resources history", "/api/resources/history", "[]")]
    [InlineData("trace get", "/api/trace", "{\"loaded\":true,\"states\":[]}")]
    [InlineData("log statistics", "/api/logs/statistics", "{\"searchAvailable\":true}")]
    [InlineData("workflow running", "/api/runs/active", "{\"items\":[],\"next\":null}")]
    public async Task DiagnosticReadsUseActualRoutesAsync(string command, string path, string response)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapGet(path, () => Results.Text(response, "application/json"));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(0, await RunAsync(app, command.Split(' ')).ConfigureAwait(true));
    }

    /// <summary>変更の元IDと不明を維持し、後続照会を別変更にしません。</summary>
    /// <returns>送信本文と結果の確認です。</returns>
    [Theory]
    [InlineData("configuration save", "/api/configuration/save")]
    [InlineData("configuration activate", "/api/configuration/activate")]
    [InlineData("trace set", "/api/trace")]
    public async Task ChangesPreserveUnknownIdentityAsync(string command, string path)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        app.MapPost(path, async (HttpContext context) =>
        {
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal(id, body.GetProperty("operationId").GetGuid());
            Assert.Equal(id.ToString("D"), context.Request.Headers["X-Contents-Request"].ToString());
            return Results.Ok(new { operationId = id, outcome = "unknown" });
        });
        string trace = "trace";
        string statusPath = command.StartsWith(trace, StringComparison.Ordinal) ? "/api/trace/operations/"
                                                                                : "/api/configuration/operations/";
        app.MapGet(statusPath + id, () => Results.Ok(new { operationId = id, outcome = "succeeded" }));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(5, await RunAsync(app, [.. command.Split(' '), "--body", "-"], JsonSerializer.Serialize(new { operationId = id })).ConfigureAwait(true));
        Assert.Equal(0, await RunAsync(app, [command.Split(' ')[0], "status", "--id", id.ToString("D")]).ConfigureAwait(true));
    }

    /// <summary>取消の操作IDと元実行IDを分け、不明応答の後は同じ取消を照会します。</summary>
    /// <returns>実HTTPを通した取消と照会の確認。</returns>
    [Fact]
    public async Task WorkflowCancellationPreservesRunAndOwnerAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        int sent = 0;
        app.MapPost("/api/runs/cancel", async (HttpContext context) =>
        {
            sent++;
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal(id, body.GetProperty("operationId").GetGuid());
            Assert.Equal(run, body.GetProperty("runId").GetGuid());
            Assert.Equal(owner, body.GetProperty("ownerInstanceId").GetGuid());
            Assert.Equal(id.ToString("D"), context.Request.Headers["X-Contents-Request"].ToString());
            return Results.Ok(new { operationId = id, runId = run, outcome = "unknown", requested = true });
        });
        app.MapGet("/api/runs/cancellations/" + id, () => Results.Ok(new { operationId = id, runId = run, outcome = "succeeded", requested = true }));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(5, await RunAsync(app, ["workflow", "cancel", "--body", "-"], JsonSerializer.Serialize(new { operationId = id, runId = run, ownerInstanceId = owner })).ConfigureAwait(true));
        Assert.Equal(0, await RunAsync(app, ["workflow", "cancel-status", "--id", id.ToString("D")]).ConfigureAwait(true));
        Assert.Equal(1, sent);
    }

    /// <summary>互換性の指摘と未取得ページを、検査成功の終了値へ読み替えません。</summary>
    /// <returns>実HTTP応答とCLI終了値の確認。</returns>
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 4)]
    [InlineData(0, true, 5)]
    public async Task CompatibilityCommandDistinguishesIssuesAndUninspectedPagesAsync(int issues, bool next, int expected)
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        string? nextSnapshot = null;
        if (next)
        {
            nextSnapshot = "next";
        }
        app.MapPost("/api/workflows/compatibility", () => Results.Ok(new { workflowId = "flow", issues = Enumerable.Range(0, issues).Select(index => new { code = "incompatible", index }), issuesOmitted = false, nextSnapshot }));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(expected, await RunAsync(app, ["workflow", "compatibility", "--body", "-"], "{\"workflowId\":\"flow\"}").ConfigureAwait(true));
    }

    /// <summary>YAMLをCore parserで完全定義へ変換してから同じPeerで意味検証します。</summary>
    /// <returns>一度ずつの解析・検証で外部操作を起こさない確認です。</returns>
    [Fact]
    public async Task YamlIsParsedThenValidatedWithoutLocalParserAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        int parsed = 0;
        int validated = 0;
        app.MapPost("/api/workflows/parse", async (HttpContext context) =>
        {
            parsed++;
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal("cluster", context.Request.Headers["X-Contents-Cluster"].ToString());
            return Results.Ok(WorkflowYamlReader.Read(body.GetProperty("text").GetString()!));
        });
        app.MapPost("/api/workflows/validate", async (HttpContext context) =>
        {
            validated++;
            JsonElement definition = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal("flow", definition.GetProperty("id").GetString());
            Assert.Equal("peer", context.Request.Headers["X-Contents-Peer"].ToString());
            string[] issues = ["unknown node type"];
            return Results.Ok(new { valid = false, issues });
        });
        await app.StartAsync().ConfigureAwait(true);
        string yaml = "id: flow\nname: Example\nnodes: []\nwires: []\n";
        Assert.Equal(4, await RunAsync(app, ["workflow", "validate", "--yaml", "-"], yaml).ConfigureAwait(true));
        Assert.Equal(1, parsed);
        Assert.Equal(1, validated);
        Assert.Equal(2, await RunAsync(app, ["workflow", "validate", "--yaml", "-", "--body", "-"], yaml).ConfigureAwait(true));
        Assert.Equal(1, parsed);
    }

    /// <summary>解析の不正応答を完全定義にせず、HTTP拒否後に意味検証を送りません。</summary>
    /// <returns>解析未達時の確認です。</returns>
    [Fact]
    public async Task ParseFailureStopsBeforeValidationAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        int validated = 0;
        app.MapPost("/api/workflows/parse", () => Results.BadRequest());
        app.MapPost("/api/workflows/validate", () =>
        {
            validated++;
            return Results.Ok(new { valid = true });
        });
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(3, await RunAsync(app, ["workflow", "validate", "--yaml", "-"], "invalid:").ConfigureAwait(true));
        Assert.Equal(0, validated);
        Assert.Equal(5, CliResponse.Classify("workflow parse", JsonSerializer.Deserialize<JsonElement>("{}"), 200));
        Assert.Equal(0, CliResponse.Classify("workflow parse", JsonSerializer.Deserialize<JsonElement>("{\"id\":\"flow\",\"nodes\":[],\"wires\":[]}"), 200));
    }

    /// <summary>競合解消のスナップショット失敗も成功に隠しません。</summary>
    [Fact]
    public void ConflictSnapshotFailureIsNotSuccessful()
    {
        JsonElement response = JsonSerializer.SerializeToElement(new
        {
            operationId = Guid.NewGuid(),
            saved = new { outcome = "succeeded" },
            shared = new { outcome = "succeeded" },
            activation = new { outcome = "succeeded" },
            snapshot = new { outcome = "failed" },
        });
        Assert.Equal(4, CliResponse.Classify("conflict resolve", response, 200));
    }

    /// <summary>HTTP 試験用の構成を所有者へ返します。</summary>
    /// <returns>動的ポートを使う試験用appです。</returns>
    private static WebApplication Create()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapGet("/client/network", () => Results.Ok(new { interfaces = Array.Empty<object>(), listening = true }));
        return app;
    }

    /// <summary>同じ明示選択でCLI要求を一回送ります。</summary>
    /// <returns>実CLI終了コードです。</returns>
    private static async Task<int> RunAsync(WebApplication app, string[] args, string body = "", CancellationToken cancellationToken = default)
    {
        using StringReader input = new(body);
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using StringWriter error = new(CultureInfo.InvariantCulture);
        return await CommandLineApplication.RunAsync(["--url", app.Urls.Single(), "--cluster", "cluster", "--peer", "peer", .. args], input, output, error, cancellationToken).ConfigureAwait(false);
    }
}
