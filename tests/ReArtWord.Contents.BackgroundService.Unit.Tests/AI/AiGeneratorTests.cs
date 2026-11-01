using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>公式API 接続線の完了・異常・非同期ジョブ境界を検証します。</summary>
public sealed class AiGeneratorTests
{
    /// <summary>toolやreasoningが先行してもメッセージの最終textを取得します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task TextReadsCompletedMessageAndRedactsSecret()
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json("""{"status":"completed","output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"result fixture-secret-not-valid"}]}]}""")))).ConfigureAwait(true);
        IActionNode node = Node(server, files, "ai.text.generate");
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(Settings(server), NodeValue.FromObject("prompt")));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("result [secret]", result.Outputs["value"].GetString());
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("/v1/responses", request.Path);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.False(body.RootElement.GetProperty("background").GetBoolean());
        Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>拒否応答と受理後に確定できない応答を混同しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(401, "{\"error\":\"fixture-secret-not-valid\"}", EOutcome.Failed)]
    [InlineData(500, "{\"error\":\"fixture-secret-not-valid\"}", EOutcome.Unknown)]
    [InlineData(200, "{}", EOutcome.Unknown)]
    [InlineData(200, "{\"status\":\"queued\"}", EOutcome.Unknown)]
    public async Task RejectionAndUncertainResponseStayDistinct(int status, string body, EOutcome outcome)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(body, status)))).ConfigureAwait(true);
        NodeResult result = await Node(server, files, "ai.text.generate").ExecuteAsync(NodeTestContext.Create(Settings(server), NodeValue.FromObject("prompt")));
        Assert.Equal(outcome, result.Outcome);
        Assert.DoesNotContain("fixture-secret-not-valid", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    /// <summary>音声と画像の最終本文を実ファイルへ確定します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("audio", "/v1/audio/speech")]
    [InlineData("picture", "/v1/images/generations")]
    public async Task GeneratedMediaIsActualProbedFile(string kind, string endpoint)
    {
        using MediaTestFiles files = new();
        string input = await files.CreateAsync(kind).ConfigureAwait(true);
        byte[] bytes = await File.ReadAllBytesAsync(input).ConfigureAwait(true);
        AiWireResponse response = new(200, "audio/wav", bytes);
        string picture = "picture";
        if (string.Equals(kind, picture, StringComparison.Ordinal))
        {
            response = AiWireResponse.Json(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(bytes) } } }));
        }
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(response))).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "generated" + Path.GetExtension(input));
        Dictionary<string, object> extra = new(StringComparer.Ordinal) { ["outputPath"] = output };
        string audio = "audio";
        if (string.Equals(kind, audio, StringComparison.Ordinal))
        {
            extra["voice"] = "coral";
        }
        NodeResult result = await Node(server, files, "ai." + kind + ".generate").ExecuteAsync(NodeTestContext.Create(Settings(server, extra), NodeValue.FromObject("prompt")));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        Assert.Equal("peer", result.Outputs["value"].Json.GetProperty("peerId").GetString());
        Assert.Equal(endpoint, Assert.Single(server.Requests).Path);
    }

    /// <summary>受理後に複数回定期照会し、生成物のdownload完了だけを成功とします。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WanPollsAndCanResumeWithoutRegenerating(bool resume)
    {
        using MediaTestFiles files = new();
        byte[] video = await File.ReadAllBytesAsync(await files.CreateAsync("video").ConfigureAwait(true)).ConfigureAwait(true);
        Uri? origin = null;
        int queries = 0;
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((request, _) =>
        {
            string create = "/api/v1/services/aigc/video-generation/video-synthesis";
            string download = "/artifact.mp4";
            if (string.Equals(request.Path, create, StringComparison.Ordinal))
            {
                return Task.FromResult(AiWireResponse.Json("""{"output":{"task_id":"job-123","task_status":"PENDING"}}"""));
            }
            if (string.Equals(request.Path, download, StringComparison.Ordinal))
            {
                return Task.FromResult(new AiWireResponse(200, "video/mp4", video));
            }
            queries++;
            if (queries < 3)
            {
                return Task.FromResult(AiWireResponse.Json("""{"output":{"task_status":"RUNNING"}}"""));
            }
            return Task.FromResult(AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { task_status = "SUCCEEDED", video_url = new Uri(origin!, "artifact.mp4").AbsoluteUri } })));
        })).ConfigureAwait(true);
        origin = server.BaseUri;
        string output = Path.Combine(files.Root, "generated.mp4");
        Dictionary<string, object> extra = new(StringComparer.Ordinal) { ["model"] = "wan2.6-t2v", ["outputPath"] = output, ["duration"] = 2, ["size"] = "1280*720", ["pollIntervalMs"] = 10 };
        if (resume)
        {
            extra["jobId"] = "job-123";
            extra.Remove("duration");
            extra.Remove("size");
        }
        NodeResult result = await Node(server, files, "ai.video.generate").ExecuteAsync(NodeTestContext.Create(Settings(server, extra), resume ? null
                                                                                                                                               : NodeValue.FromObject("prompt")));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, queries);
        Assert.Equal(video, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        Assert.Equal(!resume, server.Requests.Any(request => string.Equals(request.Method, "POST", StringComparison.Ordinal)));
        AiWireRequest downloaded = Assert.Single(server.Requests, request => string.Equals(request.Path, "/artifact.mp4", StringComparison.Ordinal));
        Assert.Null(downloaded.Authorization);
        if (!resume)
        {
            Assert.Equal("enable", Assert.Single(server.Requests, request => string.Equals(request.Method, "POST", StringComparison.Ordinal)).AsyncHeader);
        }
    }

    /// <summary>受理済みジョブの取消はremote停止を偽装しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CancellationAfterJobAcceptanceIsUnknownAndKeepsJob()
    {
        using MediaTestFiles files = new();
        TaskCompletionSource polled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((request, _) =>
        {
            string post = "POST";
            if (string.Equals(request.Method, post, StringComparison.Ordinal))
            {
                return Task.FromResult(AiWireResponse.Json("""{"output":{"task_id":"job-cancel","task_status":"PENDING"}}"""));
            }
            polled.TrySetResult();
            return Task.FromResult(AiWireResponse.Json("""{"output":{"task_status":"RUNNING"}}"""));
        })).ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        Dictionary<string, object> extra = new(StringComparer.Ordinal) { ["model"] = "wan2.6-t2v", ["outputPath"] = Path.Combine(files.Root, "cancel.mp4"), ["duration"] = 2, ["size"] = "1280*720" };
        Task<NodeResult> run = Node(server, files, "ai.video.generate").ExecuteAsync(NodeTestContext.Create(Settings(server, extra), NodeValue.FromObject("prompt")), cancellation.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = new TestTaskLifetime(run, cancellation).ConfigureAwait(true);
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);
        NodeResult result = await run.ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("job-cancel", result.Outputs["job"].Json.GetProperty("id").GetString());
        Assert.False(result.Outputs["job"].Json.GetProperty("remoteCancellationConfirmed").GetBoolean());
        Assert.Single(server.Requests, request => string.Equals(request.Method, "POST", StringComparison.Ordinal));
    }

    /// <summary>送信済み要求の期限切れを未実行へ戻さず自動再送しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task TimeoutAfterDispatchDoesNotRetryOrClaimCancellation()
    {
        using MediaTestFiles files = new();
        IterationTestClock clock = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new(async (_, token) =>
        {
            clock.Advance(10000);
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return AiWireResponse.Json("{}");
        })).ConfigureAwait(true);
        NodeResult result = await Node(server, files, "ai.text.generate").ExecuteAsync(
            NodeTestContext.Create(Settings(server), NodeValue.FromObject("prompt"), time: clock), deadline.Token);
        Assert.False(deadline.IsCancellationRequested, "受信後に進めた試験時計で処理の期限を判定できませんでした。");
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Single(server.Requests);
    }

    /// <summary>動画サービス提供元の終端失敗をdownload成功として扱いません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("FAILED", EOutcome.Failed)]
    [InlineData("CANCELED", EOutcome.Cancelled)]
    [InlineData("UNKNOWN", EOutcome.Unknown)]
    public async Task WanTerminalAndUnknownStatusesRemainDistinct(string status, EOutcome outcome)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { task_status = status, message = "fixture-secret-not-valid" } }))))).ConfigureAwait(true);
        Dictionary<string, object> extra = new(StringComparer.Ordinal) { ["model"] = "wan2.6-t2v", ["outputPath"] = Path.Combine(files.Root, "failed.mp4"), ["duration"] = 2, ["size"] = "1280*720", ["jobId"] = "existing-job" };
        NodeResult result = await Node(server, files, "ai.video.generate").ExecuteAsync(NodeTestContext.Create(Settings(server, extra), NodeValue.FromObject("prompt")));
        Assert.Equal(outcome, result.Outcome);
        Assert.Single(server.Requests);
        Assert.DoesNotContain("fixture-secret-not-valid", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(files.Root, "failed.mp4")));
    }

    /// <summary>指定typeの公開生成処理成果を取得します。</summary>
    /// <returns>実際に登録されたノードです。</returns>
    internal static IActionNode Node(AiWireFixture server, MediaTestFiles files, string type)
    {
        return (IActionNode)Assert.Single(AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(server.BaseUri), files.Options), node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>明示ループバック NICと試験用の構成秘密参照を設定します。</summary>
    /// <returns>追加設定を反映したJSONです。</returns>
    internal static string Settings(AiWireFixture server, IReadOnlyDictionary<string, object>? extra = null)
    {
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["model"] = "fixture-model", ["secretRef"] = "fixture", ["interfaceId"] = server.InterfaceId, ["localAddress"] = "127.0.0.1", ["timeoutMs"] = 10000 };
        if (extra is not null)
        {
            foreach ((string name, object value) in extra)
            {
                settings[name] = value;
            }
        }
        return JsonSerializer.Serialize(settings);
    }
}
