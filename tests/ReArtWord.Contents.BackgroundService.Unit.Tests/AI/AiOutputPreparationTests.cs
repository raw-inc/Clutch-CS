using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>送信前の出力準備エラーを、API未送信の具体的な理由として返します。</summary>
public sealed class AiOutputPreparationTests
{
    /// <summary>各音声サービスで親を再帰作成し、その後で一度だけ生成要求を送ります。</summary>
    /// <returns>サービスに依存しないディレクトリ作成の確認です。</returns>
    [Theory]
    [InlineData("ai.audio.qwen.generate", "qwen3-tts-vc-2026-01-22")]
    [InlineData("ai.audio.generate", "gpt-4o-mini-tts")]
    [InlineData("ai.audio.elevenlabs.generate", "eleven_multilingual_v2")]
    [InlineData("ai.audio.elevenlabs.v3.generate", "eleven_v3")]
    [InlineData("ai.audio.elevenlabs.v4.generate", "eleven_v4")]
    [InlineData("ai.audio.qwen.tts.generate", "qwen3-tts-flash")]
    public async Task MissingParentsAreCreatedBeforeDispatchAsync(string type, string model)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}", 401)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = new(StringComparer.Ordinal)
        {
            ["model"] = model, ["voice"] = "registered-voice", ["outputPath"] = Path.Combine(files.Root, "missing", "group", "audio.wav"),
        };
        NodeResult result = await AiGeneratorTests.Node(server, files, type).ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Single(server.Requests);
        Assert.DoesNotContain("fixture-secret-not-valid", result.Message!, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(files.Root, "missing", "group")));
    }

    /// <summary>既存の出力とffprobe未配置を区別し、生成や上書きを開始しません。</summary>
    /// <returns>ローカル前提条件ごとの安定した理由です。</returns>
    [Theory]
    [InlineData(false, "ai.outputIsDirectory")]
    [InlineData(true, "ai.ffprobeUnavailable")]
    public async Task ExistingOutputAndMissingProbeHaveSpecificReasonsAsync(bool missingProbe, string expected)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        string output = Path.Combine(files.Root, "existing.wav");
        Directory.CreateDirectory(output);
        MediaOptions media = files.Options;
        if (missingProbe)
        {
            output = Path.Combine(files.Root, "new.wav");
            media = media with { FfprobePath = Path.Combine(files.Root, "absent-ffprobe.exe") };
        }
        IActionNode node = (IActionNode)AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(server.BaseUri), media).Single(item => item.Descriptor.Type is "ai.audio.qwen.generate");
        Dictionary<string, object> settings = new(StringComparer.Ordinal) { ["model"] = "qwen3-tts-vc-2026-01-22", ["voice"] = "voice", ["outputPath"] = output };
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(expected, result.ReasonCode);
        Assert.Empty(server.Requests);
        Assert.True(Directory.Exists(Path.Combine(files.Root, "existing.wav")));
    }

    /// <summary>DashScope SDK向けベースURLを完全HTTPエンドポイントと取り違えません。</summary>
    [Theory]
    [InlineData("https://workspace.ap-southeast-1.maas.aliyuncs.com/api/v1", false)]
    [InlineData("https://dashscope-intl.aliyuncs.com/api/v1/", false)]
    [InlineData("https://workspace.ap-southeast-1.maas.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", true)]
    [InlineData("https://proxy.example/api/v1", true)]
    public void QwenEndpointSeparatesProviderBaseUrlFromCustomRoutes(string endpoint, bool valid)
    {
        using MediaTestFiles files = new();
        INode node = AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(), files.Options).Single(item => item.Descriptor.Type is "ai.audio.qwen.generate");
        var definition = new ReArtWord.Contents.BackgroundService.Contracts.Workflow.NodeDefinition("speech", node.Descriptor.Type,
            JsonSerializer.SerializeToElement(new { model = "qwen3-tts-vc-2026-01-22", voice = "voice", interfaceId = "nic", localAddress = "127.0.0.1", outputPath = Path.Combine(files.Root, "out.wav"), endpoint }));
        Assert.Equal(valid, !node.Validate(definition).Any(issue => issue.Code is "ai.qwen.endpoint.path"));
    }
}
