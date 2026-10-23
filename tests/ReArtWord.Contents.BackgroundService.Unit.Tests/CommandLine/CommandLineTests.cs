using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.CommandLine;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.CommandLine;

/// <summary>実HTTP経由でCLIの送信と終了結果を確認します。</summary>
public sealed class CommandLineTests
{
    /// <summary>不正なZIP断片で例外終了せず、保存途中のファイルを完成品として残しません。</summary>
    /// <returns>全体ハッシュ不一致時の終了コードと後始末です。</returns>
    [Fact]
    public async Task ArchiveHashMismatchDoesNotLeaveACompletedFile()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid operation = Guid.NewGuid();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1 }));
        app.MapPost("/api/archive/export", () => Results.Ok(new { operationId = operation, stage = "completed", completed = true, outcome = "succeeded",
            file = new { operationId = operation, length = 1, sha256 = hash } }));
        app.MapPost("/api/archive/read", () => Results.Ok(new { operationId = operation, offset = 0, dataBase64 = "Ag==" }));
        await app.StartAsync().ConfigureAwait(true);
        string path = Path.Combine(Path.GetTempPath(), "ClutchArchive-" + operation.ToString("N") + ".zip");
        try
        {
            (int code, _, string error) = await RunAsync(["--url", app.Urls.Single(), "archive", "export", "--file", path, "--operation-id", operation.ToString("D")]).ConfigureAwait(true);
            Assert.Equal(2, code);
            Assert.Contains(nameof(InvalidDataException), error, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".partial-" + operation.ToString("N")));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".partial-" + operation.ToString("N"));
        }
    }

    /// <summary>接続先の状態取得と同一操作IDの本文・照会を確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task StatusAndRunPreserveOperationIdAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid operation = Guid.NewGuid();
        app.MapGet("/api/status", () => Results.Ok(new { peerId = "actual-peer" }));
        app.MapPost("/api/runs", async (HttpContext context) =>
        {
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal(operation, body.GetProperty("operationId").GetGuid());
            Assert.Equal(operation.ToString("D"), context.Request.Headers["X-Contents-Request"].ToString());
            Assert.Equal("test-cluster", context.Request.Headers["X-Contents-Cluster"].ToString());
            Assert.Equal("test-peer", context.Request.Headers["X-Contents-Peer"].ToString());
            Assert.Equal("operator", context.Request.Headers["X-Contents-Display-Name"].ToString());
            Assert.Equal("workflow-1", body.GetProperty("workflowId").GetString());
            return Results.Accepted("/api/runs/" + operation, new { operationId = operation, outcome = "unknown", running = true });
        });
        app.MapGet("/api/runs/{id}", (string id) => Results.Ok(new { operationId = id, outcome = "succeeded", running = false }));
        await app.StartAsync().ConfigureAwait(true);
        string url = app.Urls.Single();
        (int code, string output, _) = await RunAsync(["--url", url, "status"]).ConfigureAwait(true);
        Assert.Equal(0, code);
        Assert.Contains("actual-peer", output, StringComparison.Ordinal);
        (int runCode, string runOutput, string runError) = await RunAsync(["--url", url, "--display-name", "operator", "workflow", "run", "--body", "-"], JsonSerializer.Serialize(new { operationId = operation, workflowId = "workflow-1" })).ConfigureAwait(true);
        Assert.Equal(5, runCode);
        Assert.Contains(operation.ToString("D"), runOutput, StringComparison.Ordinal);
        Assert.Contains(operation.ToString("D"), runError, StringComparison.Ordinal);
        Assert.Equal(0, (await RunAsync(["--url", url, "run-status", "--id", operation.ToString("D")]).ConfigureAwait(true)).Code);
    }

    /// <summary>秘密がAPIからechoされても通常出力・errorへ表示しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecretBodyNeverAppearsInOutputAsync(bool fromEnvironment)
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        string secret = "private-api-value-765";
        app.MapPut("/api/secrets", async (HttpContext context) =>
        {
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal(secret, body.GetProperty("value").GetString());
            return Results.Ok(new { saved = true, echo = secret });
        });
        await app.StartAsync().ConfigureAwait(true);
        string variable = "CONTENTS_CLI_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            string[] source = fromEnvironment ? ["--env", variable]
                                              : ["--stdin"];
            (int code, string output, string error) = await RunAsync(["--url", app.Urls.Single(), "secret", "set", "--name", "api", .. source], secret).ConfigureAwait(true);
            Assert.Equal(0, code);
            Assert.DoesNotContain(secret, output + error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
        Assert.Equal(2, (await RunAsync(["--url", app.Urls.Single(), "secret", "set", "--name", "api", "--value", secret]).ConfigureAwait(true)).Code);
    }

    /// <summary>HTTP成功と業務の不明・部分成功を区別します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("{\"valid\":false}", 200, "workflow validate", 4)]
    [InlineData("{\"outcome\":\"unknown\"}", 200, "source stop", 5)]
    [InlineData("{\"outcome\":\"failed\"}", 200, "source stop", 4)]
    [InlineData("{\"saved\":{\"outcome\":\"succeeded\"},\"shared\":{\"outcome\":\"unknown\"},\"snapshot\":{\"outcome\":\"succeeded\"},\"activation\":{\"outcome\":\"notExecuted\"}}", 200, "workflow save", 5)]
    [InlineData("{\"complete\":false}", 200, "log query", 4)]
    [InlineData("failure", 503, "source stop", 3)]
    [InlineData("{\"entries\":[],\"cursor\":\"next-page\"}", 200, "workflow query", 0)]
    [InlineData("{\"entries\":[],\"cursor\":\"next-page\"}", 200, "execution query", 0)]
    [InlineData("{\"entries\":[],\"cursor\":\"next-page\"}", 200, "snapshot query", 0)]
    [InlineData("{\"entries\":[],\"cursor\":\"next-page\"}", 200, "conflict query", 0)]
    [InlineData("{\"conflict\":null,\"detailReason\":\"Conflict.DetailTooLarge\"}", 200, "conflict detail", 4)]
    [InlineData("{\"entries\":[],\"cursor\":\"next\"}", 200, "workflow detached-query", 0)]
    public async Task NonSuccessResponsesStayNonzeroAsync(string body, int status, string command, int expected)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapPost("/{**path}", () => Results.Text(body, "application/json", statusCode: status));
        await app.StartAsync().ConfigureAwait(true);
        string[] args = ["--url", app.Urls.Single(), .. command.Split(' '), "--body", "-"];
        Assert.Equal(expected, (await RunAsync(args, "{}").ConfigureAwait(true)).Code);
    }

    /// <summary>参加要求の受付を同期完了と偽りません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RejoinIsPendingUntilReadyAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapPost("/api/cluster/rejoin", () => Results.Ok(new { status = "syncing" }));
        app.MapPost("/api/cluster/detach", () => Results.Ok(new { status = "detached" }));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(5, (await RunAsync(["--url", app.Urls.Single(), "cluster", "rejoin"]).ConfigureAwait(true)).Code);
        Assert.Equal(0, (await RunAsync(["--url", app.Urls.Single(), "cluster", "detach"]).ConfigureAwait(true)).Code);
    }

    /// <summary>API停止を成功扱いせず、不正IDは送信前に拒否します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task OfflineAndMismatchedIdsAreExplicitAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        await app.StartAsync().ConfigureAwait(true);
        string url = app.Urls.Single();
        await app.StopAsync().ConfigureAwait(true);
        Assert.Equal(6, (await RunAsync(["--url", url, "status"]).ConfigureAwait(true)).Code);
        Assert.Equal(6, (await RunAsync(["--url", url, "workflow", "run", "--body", "-"], "{}").ConfigureAwait(true)).Code);
        Assert.Equal(2, (await RunAsync(["--url", url, "workflow", "run", "--operation-id", Guid.NewGuid().ToString("D"), "--body", "-"], JsonSerializer.Serialize(new { operationId = Guid.NewGuid() })).ConfigureAwait(true)).Code);
    }

    /// <summary>追加した管理操作を実HTTPの経路と固定IDへ接続します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("snapshot capture-status", "/api/snapshots/captures/", false, 0)]
    [InlineData("snapshot restore-status", "/api/snapshots/restores/", false, 0)]
    [InlineData("file operation", "/api/files/operations/", false, 0)]
    [InlineData("file recover", "/api/files/operations/", false, 0)]
    [InlineData("snapshot recover", "/api/snapshots/recover", true, 0)]
    [InlineData("safety set", "/api/safety", true, 0)]
    [InlineData("update start", "/api/updates/start", true, 5)]
    [InlineData("update status", "/api/updates/", false, 0)]
    [InlineData("safety status", "/api/safety/operations/", false, 0)]
    [InlineData("conflict status", "/api/conflicts/operations/", false, 0)]
    [InlineData("conflict resolve", "/api/conflicts/resolve", true, 0)]
    [InlineData("workflow detached-resolve", "/api/workflows/detached/resolve", true, 0)]
    [InlineData("workflow detached-status", "/api/workflows/detached/operations/", false, 0)]
    public async Task AddedOperationsUseActualRoutesAsync(string command, string route, bool bodyRequired, int expected)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        string expectedPath = bodyRequired ? route
                                           : route + id.ToString("D");
        string recover = "file recover";
        if (string.Equals(command, recover, StringComparison.Ordinal))
        {
            expectedPath += "/recover";
        }
        app.MapMethods("/{**path}", ["GET", "POST"], async (HttpContext context) =>
        {
            Assert.Equal(expectedPath, context.Request.Path.Value);
            if (bodyRequired)
            {
                JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
                Assert.Equal(id, body.GetProperty("operationId").GetGuid());
                Assert.Equal("original-version", body.GetProperty("expectedVersion").GetString());
                Assert.True(body.GetProperty("variants")[0].GetProperty("retained").GetBoolean());
            }
            string capture = "snapshot capture-status";
            if (string.Equals(command, capture, StringComparison.Ordinal))
            {
                return Results.Ok(new { operationId = id, result = new { result = new { outcome = "succeeded" } } });
            }
            string updateStart = "update start";
            string updateStatus = "update status";
            if (string.Equals(command, updateStart, StringComparison.Ordinal) || string.Equals(command, updateStatus, StringComparison.Ordinal))
            {
                return Results.Ok(new { request = new { operationId = id }, outcome = "succeeded", completed = expected == 0 });
            }
            return Results.Ok(new { operationId = id, outcome = "succeeded" });
        });
        await app.StartAsync().ConfigureAwait(true);
        string[] options = bodyRequired ? ["--body", "-"]
                                        : ["--id", id.ToString("D")];
        string input = JsonSerializer.Serialize(new { operationId = id, expectedVersion = "original-version", variants = new[] { new { retained = true } } });
        (int code, string output, _) = await RunAsync(["--url", app.Urls.Single(), .. command.Split(' '), .. options], input).ConfigureAwait(true);
        Assert.Equal(expected, code);
        Assert.Contains(id.ToString("D"), output, StringComparison.Ordinal);
    }

    /// <summary>取得不能、別操作の応答とIDなし回復を成功扱いしません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UnknownOperationAndRecoveryMismatchStayUnconfirmedAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapGet("/api/snapshots/restores/{id}", () => Results.Ok((object?)null));
        app.MapGet("/api/snapshots/captures/{id}", (string id) => Results.Ok(new { operationId = id, result = (object?)null }));
        app.MapGet("/api/updates/{id}", () => Results.Ok(new { request = new { operationId = Guid.NewGuid() }, outcome = "succeeded", completed = true }));
        app.MapGet("/api/safety", () => Results.Ok(new { loaded = false, states = Array.Empty<object>() }));
        await app.StartAsync().ConfigureAwait(true);
        string url = app.Urls.Single();
        foreach (string command in new[] { "snapshot restore-status", "snapshot capture-status", "update status" })
        {
            Assert.Equal(5, (await RunAsync(["--url", url, .. command.Split(' '), "--id", Guid.NewGuid().ToString("D")]).ConfigureAwait(true)).Code);
        }
        Assert.Equal(5, (await RunAsync(["--url", url, "safety", "list"]).ConfigureAwait(true)).Code);
        Assert.Equal(2, (await RunAsync(["--url", url, "snapshot", "recover", "--body", "-"], "{}").ConfigureAwait(true)).Code);
        Assert.Equal(2, (await RunAsync(["--url", url, "file", "recover", "--id", Guid.NewGuid().ToString("D"), "--operation-id", Guid.NewGuid().ToString("D")]).ConfigureAwait(true)).Code);
    }

    /// <summary>単体ワークフローの本文省略を完全な取得成功としません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task OmittedWorkflowBodyIsExplicitAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapGet("/api/workflows/{id}", () => Results.Ok(new { saved = (object?)null, summary = new { id = "flow" }, detailReason = "workflow.details.size" }));
        await app.StartAsync().ConfigureAwait(true);
        (int code, string output, _) = await RunAsync(["--url", app.Urls.Single(), "workflow", "get", "--id", "flow"]).ConfigureAwait(true);
        Assert.Equal(4, code);
        Assert.Contains("workflow.details.size", output, StringComparison.Ordinal);
    }

    /// <summary>競合解消の四段階・不明・誤IDを実HTTP応答で区別します。</summary>
    /// <returns>CLI経路と終了値の確認です。</returns>
    [Theory]
    [InlineData("succeeded", false, 0)]
    [InlineData("unknown", false, 5)]
    [InlineData("failed", false, 4)]
    [InlineData("succeeded", true, 5)]
    public async Task ConflictResolutionPhasesAndIdentityAreVerifiedAsync(string shared, bool wrongIdentity, int expected)
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        app.MapPost("/api/conflicts/resolve", async (HttpContext context) =>
        {
            JsonElement body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.Equal(id, body.GetProperty("operationId").GetGuid());
            Assert.Equal(id.ToString("D"), context.Request.Headers["X-Contents-Request"].ToString());
            Assert.Equal("v2", body.GetProperty("expectedVersions")[1].GetString());
            Guid returned = id;
            if (wrongIdentity)
            {
                returned = Guid.NewGuid();
            }
            return Results.Ok(new { operationId = returned, saved = new { outcome = "succeeded" }, shared = new { outcome = shared }, activation = new { outcome = "succeeded" }, snapshot = new { outcome = "succeeded" }, issues = Array.Empty<object>() });
        });
        await app.StartAsync().ConfigureAwait(true);
        string[] versions = ["v1", "v2"];
        string body = JsonSerializer.Serialize(new { operationId = id, conflictId = "conflict", expectedVersions = versions, selectedVersion = "v2" });
        Assert.Equal(expected, (await RunAsync(["--url", app.Urls.Single(), "conflict", "resolve", "--body", "-"], body).ConfigureAwait(true)).Code);
    }

    /// <summary>ワークフロー IDをGUIDと誤解せず、ローカル未共有を共有成功へ変更しません。</summary>
    /// <returns>実経路とローカル結果の確認です。</returns>
    [Fact]
    public async Task DetachedDetailAndLocalOnlyResultsRemainExplicitAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapGet("/api/workflows/local-flow/detached", () => Results.Ok(new { state = new { workflowId = "local-flow" } }));
        app.MapPost("/api/workflows/detached/resolve", async (HttpContext context) =>
        {
            JsonElement request = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(true);
            Assert.True(request.GetProperty("currentRevisionConfirmed").GetBoolean());
            Assert.Equal("current", request.GetProperty("expectedCurrentRevision").GetString());
            return Results.Ok(new { candidate = (object?)null, conflicts = Array.Empty<object>(), validation = Array.Empty<object>(), save = new { operationId = request.GetProperty("operationId").GetGuid(), saved = new { outcome = "succeeded" }, shared = new { outcome = "notExecuted", reason = "Detached.Discarded" }, snapshot = new { outcome = "notExecuted" }, activation = new { outcome = "notExecuted" }, localOnly = true } });
        });
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(0, (await RunAsync(["--url", app.Urls.Single(), "workflow", "detached-detail", "--id", "local-flow"]).ConfigureAwait(true)).Code);
        string request = JsonSerializer.Serialize(new { operationId = Guid.NewGuid(), workflowId = "local-flow", expectedLocalRevision = "local", expectedCurrentRevision = "current", currentRevisionConfirmed = true, resolutions = new { }, discard = true });
        (int code, string output, _) = await RunAsync(["--url", app.Urls.Single(), "workflow", "detached-resolve", "--body", "-"], request).ConfigureAwait(true);
        Assert.Equal(4, code);
        Assert.Contains("localOnly", output, StringComparison.Ordinal);
    }

    /// <summary>外部ポートの競合を避ける実HTTP 試験用の構成を生成します。</summary>
    /// <returns>呼出元が所有するWebApplicationです。</returns>
    private static WebApplication Create()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapGet("/client/network", () => Results.Ok(new { interfaces = Array.Empty<object>(), listening = true }));
        return app;
    }

    /// <summary>標準入出力を捕捉して一回のCLIを実行します。</summary>
    /// <returns>終了コードと通常出力・errorです。</returns>
    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] args, string input = "", CancellationToken cancellationToken = default)
    {
        string[] selected = [.. args, "--cluster", "test-cluster", "--peer", "test-peer"];
        using StringReader reader = new(input);
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using StringWriter error = new(CultureInfo.InvariantCulture);
        int code = await CommandLineApplication.RunAsync(selected, reader, output, error, cancellationToken).ConfigureAwait(true);
        return (code, output.ToString(), error.ToString());
    }
}
