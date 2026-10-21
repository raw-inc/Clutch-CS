using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>他ノードから届いた生成設定を、実グラフとHTTP要求で確認します。</summary>
public sealed class RuntimeAiSettingsTests
{
    private static readonly string[] speechSettings = ["voice", "outputPath", "languageType", "endpoint"];

    /// <summary>登録済みVoice ID・保存先・言語を一回ごとに固定し、静的文字列を要求しません。</summary>
    /// <returns>二回の合成要求と取得したWAVの確認です。</returns>
    [Fact]
    public async Task QwenAcceptsRuntimeVoiceAndOutputSettingsAsync()
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        Uri? origin = null;
        string get = "GET";
        AiWireFixture server = new((request, _) =>
        {
            if (string.Equals(request.Method, get, StringComparison.Ordinal))
            {
                return Task.FromResult(new AiWireResponse(200, "audio/wav", wave));
            }
            return Task.FromResult(AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { finish_reason = "stop", audio = new { url = new Uri(origin!, "speech.wav").AbsoluteUri } } })));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        origin = server.BaseUri;
        int count = 0;
        WorkflowFixture.ActionNode supply = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new
        {
            voice = "registered-voice-" + ++count,
            outputPath = Path.Combine(files.Root, "generated-" + count + ".wav"),
            languageType = "Japanese",
            endpoint = new Uri(origin, "api/v1/services/aigc/multimodal-generation/generation").AbsoluteUri,
        }))));
        WorkflowFixture fixture = new(supply, AiGeneratorTests.Node(server, files, "ai.audio.qwen.generate"));
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition target = new("speech", "ai.audio.qwen.generate", JsonSerializer.Deserialize<JsonElement>(AiGeneratorTests.Settings(server,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["model"] = "qwen3-tts-vc-2026-01-22" })),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("こんにちは") }, new(EExecutionScope.One, ["a"]));
        WorkflowDefinition document = new("speech-flow", "Speech", [WorkflowFixture.Definition("supply"), target],
            speechSettings.Select(key => new WireDefinition("supply", "value", target.Id, SettingPorts.Name(key), Path: "/" + key)).ToArray());
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        Assert.Empty(server.Requests);
        for (int number = 1; number <= 2; number++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "supply").ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, target.Id, StringComparison.Ordinal)).Outcome);
            Assert.Equal(wave, await File.ReadAllBytesAsync(Path.Combine(files.Root, "generated-" + number + ".wav")).ConfigureAwait(true));
        }
        AiWireRequest[] requests = server.Requests.Where(request => string.Equals(request.Method, "POST", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, requests.Length);
        for (int index = 0; index < requests.Length; index++)
        {
            using JsonDocument body = JsonDocument.Parse(requests[index].Body);
            Assert.Equal("registered-voice-" + (index + 1), body.RootElement.GetProperty("input").GetProperty("voice").GetString());
            Assert.Equal("Japanese", body.RootElement.GetProperty("input").GetProperty("language_type").GetString());
        }
        Assert.False(target.Settings.TryGetProperty("voice", out _));
    }

    /// <summary>数値・真偽値・配列・Objectも、任意API設定の型を保って送ります。</summary>
    /// <returns>既定設定に残る不正なendpointを送信しないことも確認します。</returns>
    [Theory]
    [InlineData("ai.openai.chat", false)]
    [InlineData("ai.openai.responses", true)]
    public async Task TextGenerationUsesRuntimeStructuredOptionsAsync(string type, bool responses)
    {
        using MediaTestFiles files = new();
        string response = """{"choices":[{"finish_reason":"stop","message":{"content":"done"}}]}""";
        if (responses)
        {
            response = """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"done"}]}]}""";
        }
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json(response)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        Dictionary<string, object> supplied = new(StringComparer.Ordinal)
        {
            ["model"] = "runtime-model", ["temperature"] = 0.3, ["store"] = false,
            ["metadata"] = new { request = "dynamic" }, ["tools"] = Array.Empty<object>(),
            ["endpoint"] = new Uri(server.BaseUri, "generate").AbsoluteUri,
        };
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(supplied)))), AiGeneratorTests.Node(server, files, type));
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition target = new("generate", type, JsonSerializer.SerializeToElement(new { interfaceId = server.InterfaceId, localAddress = "127.0.0.1", endpoint = "invalid-stored-endpoint" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("hello") }, new(EExecutionScope.One, ["a"]));
        WorkflowDefinition document = new("text-flow", "Text", [WorkflowFixture.Definition("supply"), target],
            supplied.Keys.Select(key => new WireDefinition("supply", "value", target.Id, SettingPorts.Name(key), Path: "/" + key)).ToArray());
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "supply").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, target.Id, StringComparison.Ordinal)).Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal("runtime-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.3, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("dynamic", body.RootElement.GetProperty("metadata").GetProperty("request").GetString());
        Assert.Empty(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Null(request.Authorization);
    }
}
