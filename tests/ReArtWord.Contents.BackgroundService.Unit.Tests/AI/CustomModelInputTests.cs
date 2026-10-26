using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>候補外のモデル名を設定端子から送り、API要求と送信前の検証を確認します。</summary>
public sealed class CustomModelInputTests
{
    /// <summary>モデル候補を持つ全サービスで、接続値が保存値より優先されます。</summary>
    /// <returns>外部サービスを使わず、二回のHTTP本文を確認します。</returns>
    [Theory]
    [InlineData("ai.audio.qwen.generate", "model")]
    [InlineData("ai.audio.qwen.tts.generate", "model")]
    [InlineData("ai.audio.qwen.audio3.generate", "model")]
    [InlineData("ai.audio.elevenlabs.generate", "model_id")]
    [InlineData("ai.audio.elevenlabs.v3.generate", "model_id")]
    [InlineData("ai.audio.elevenlabs.v4.generate", "model_id")]
    [InlineData("ai.audio.elevenlabs.soundEffects", "model_id")]
    [InlineData("ai.video.generate", "model")]
    public async Task ConnectedModelNamesReachTheProviderUnchangedAsync(string type, string field)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(new AiWireResponse(422, "application/json", Encoding.UTF8.GetBytes("{}"))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        IActionNode node = AiGeneratorTests.Node(server, files, type);
        int number = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("custom.model/vNext:" + ++number)))), node);
        fixture.Executor.FileExecutionHandler = static (operation, context, token) => operation.ExecuteAsync(context, token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition target = Target(server, files, node);
        CompilationResult compiled = fixture.Compiler.Compile(Document(target));
        Assert.Empty(compiled.Issues);
        for (int index = 1; index <= 2; index++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "supply").ConfigureAwait(true);

            // HTTP拒否を返す試験サーバーまで到達したことを確認し、生成や再試行は行いません。
            Assert.Equal(EOutcome.Failed, Assert.Single(report.Nodes, item => string.Equals(item.NodeId, target.Id, StringComparison.Ordinal)).Outcome);
            Assert.Equal(index, server.Requests.Count);
            using JsonDocument body = JsonDocument.Parse(server.Requests.Last().Body);
            Assert.Equal("custom.model/vNext:" + index, body.RootElement.GetProperty(field).GetString());
        }
        Assert.NotEqual("custom.model/vNext:2", target.Settings.GetProperty("model").GetString());
    }

    /// <summary>モデル候補を自由入力にしても、空文字や数値をAPIへ送りません。</summary>
    /// <returns>保存済みモデルへの暗黙の切替も起きないことの確認です。</returns>
    [Theory]
    [InlineData("ai.audio.qwen.generate", true)]
    [InlineData("ai.audio.qwen.tts.generate", true)]
    [InlineData("ai.audio.qwen.audio3.generate", true)]
    [InlineData("ai.audio.elevenlabs.generate", true)]
    [InlineData("ai.audio.elevenlabs.v3.generate", true)]
    [InlineData("ai.audio.elevenlabs.v4.generate", true)]
    [InlineData("ai.audio.elevenlabs.soundEffects", false)]
    [InlineData("ai.video.generate", true)]
    public async Task InvalidConnectedModelsAreRejectedBeforeDispatchAsync(string type, bool required)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        IActionNode node = AiGeneratorTests.Node(server, files, type);
        NodeValue supplied = NodeValue.FromObject(string.Empty);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(supplied))), node);
        fixture.Executor.FileExecutionHandler = static (operation, context, token) => operation.ExecuteAsync(context, token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition target = Target(server, files, node);
        CompilationResult compiled = fixture.Compiler.Compile(Document(target));
        Assert.Empty(compiled.Issues);
        List<NodeValue> invalid = [NodeValue.FromObject(string.Empty), NodeValue.FromObject(42)];
        if (required)
        {
            invalid.Add(NodeValue.FromObject((string?)null));
        }
        foreach (NodeValue value in invalid)
        {
            supplied = value;
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "supply").ConfigureAwait(true);
            NodeExecutionReport result = Assert.Single(report.Nodes, item => string.Equals(item.NodeId, target.Id, StringComparison.Ordinal));
            Assert.Equal(EOutcome.NotExecuted, Assert.Single(result.Targets).Result.Outcome);
            Assert.Empty(server.Requests);
        }
    }

    /// <summary>各APIに必要な固定設定を揃え、モデルだけを接続で上書きします。</summary>
    /// <returns>既存のモデル選択を保存した実行ノードです。</returns>
    private static NodeDefinition Target(AiWireFixture server, MediaTestFiles files, IActionNode node)
    {
        JsonElement properties = node.Descriptor.SettingsSchema.GetProperty("properties");
        string saved = properties.GetProperty("model").GetProperty("examples")[0].GetString()!;
        string extension = node.Descriptor.Type switch
        {
            "ai.video.generate" => ".mp4",
            _ when node.Descriptor.Type.Contains("elevenlabs", StringComparison.Ordinal) => ".mp3",
            _ => ".wav",
        };
        Dictionary<string, object> settings = new(StringComparer.Ordinal)
        {
            ["model"] = saved,
            ["outputPath"] = Path.Combine(files.Root, "generated" + extension),
        };
        foreach ((string name, object value) in new (string, object)[] { ("voice", "fixture_voice"), ("workspaceId", "fixture-workspace"), ("duration", 2), ("size", "1280*720") })
        {
            if (properties.TryGetProperty(name, out _))
            {
                settings[name] = value;
            }
        }
        return new("generate", node.Descriptor.Type, JsonSerializer.Deserialize<JsonElement>(AiGeneratorTests.Settings(server, settings)),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("hello") }, new(EExecutionScope.One, ["a"]));
    }

    /// <summary>Stringの実行結果をモデル設定へ接続します。</summary>
    /// <returns>保存値と接続値の優先順を検証するグラフです。</returns>
    private static WorkflowDefinition Document(NodeDefinition target) => new("model-flow", "Model input", [WorkflowFixture.Definition("supply"), target],
        [new("supply", "value", target.Id, SettingPorts.Name("model"))]);
}
