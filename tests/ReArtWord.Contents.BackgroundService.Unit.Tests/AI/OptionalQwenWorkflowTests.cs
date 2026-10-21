using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>PC候補を引き継ぐ反復内でも、Qwenの任意パラメーターを省略して生成できます。</summary>
public sealed class OptionalQwenWorkflowTests
{
    /// <summary>欠落・null・明示値を同じ反復へ混ぜ、各HTTP本文と完成ファイルを確認します。</summary>
    /// <returns>実ランタイム・HTTP・WAV保存を通した再現試験です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IterationKeepsPcCandidatesAndOmitsOnlyAbsentParametersAsync(bool parallel)
    {
        using MediaTestFiles files = new();
        byte[] audio = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        Uri? origin = null;
        AiWireFixture server = new((request, _) => Task.FromResult(request.Method switch
        {
            "GET" => new AiWireResponse(200, "audio/wav", audio),
            _ => AiWireResponse.Json(JsonSerializer.Serialize(new { output = new { finish_reason = "stop", audio = new { url = new Uri(origin!, "/audio").AbsoluteUri } } })),
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        origin = server.BaseUri;
        IActionNode qwen = AiGeneratorTests.Node(server, files, "ai.audio.qwen.audio3.generate");
        WorkflowFixture fixture = new(new ButtonNode(), new PcSelectionNode(), new ValueMergeNode(), new ForEachNode(parallel), new ForEachEndNode(), qwen,
            new WorkflowFixture.ActionNode((context, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(Path.Combine(files.Root,
                context.Inputs["value"].Json.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav"))))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);

        // 試験ごとの独立した出力先を使い、ファイル操作の境界から実AIノードへ渡します。
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        JsonElement empty = NodeSettings.Schema("{}");
        JsonElement settings = NodeSettings.Schema(AiGeneratorTests.Settings(server, new Dictionary<string, object>
        {
            ["model"] = "qwen-audio-3.0-tts-flash", ["voice"] = "longanhuan_v3.6", ["workspaceId"] = "fixture-workspace",
            ["outputPath"] = Path.Combine(files.Root, "unused.wav"), ["instructions"] = "old instruction", ["volume"] = 80, ["pitch"] = 1.1, ["enableSsml"] = true,
        }));
        string loopType = IterationNodeTypes.ForEach;
        if (parallel)
        {
            loopType = IterationNodeTypes.ParallelForEach;
        }
        WorkflowDefinition definition = new("qwen-optional", "qwen optional", [new("button", "control.button", empty),
            new("pc", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-a"]}""")),
            new("merge", ValueMergePorts.NodeType, empty), new("loop", loopType, empty), WorkflowFixture.Definition("path"),
            new("qwen", qwen.Descriptor.Type, settings), new("end", IterationNodeTypes.End, empty)],
            [new("button", "start", "pc", "trigger"), new("pc", "value", "merge", "input1"), new("merge", "value", "qwen", PcSelectionPorts.Execution),
                new("button", "value", "loop", "value"), new("button", "start", "loop", "trigger"), new("loop", "index", "path", "value"),
                new("path", "value", "qwen", "$settings.outputPath"), new("loop", "item/text", "qwen", "value"),
                new("loop", "item/feature", "qwen", "$settings.instructions"), new("loop", "item/volume", "qwen", "$settings.volume"),
                new("loop", "item/pitch", "qwen", "$settings.pitch"), new("loop", "item/ssml", "qwen", "$settings.enableSsml"),
                new("loop", "loop", "end", "loop"), new("qwen", CompletionPorts.Success, "end", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        NodeValue values = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("""
            [{"text":"missing"},{"text":"null","feature":null,"volume":null,"pitch":null,"ssml":null},
             {"text":"explicit","feature":"calm","volume":0,"pitch":1.2,"ssml":false}]
            """));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button", new Dictionary<string, NodeValue> { ["value"] = values }).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report.Nodes.Select(node => new { node.NodeId, node.Outcome, Reasons = node.Targets.Select(target => target.Result.ReasonCode) })));
        AiWireRequest[] generated = server.Requests.Where(request => request.Method is "POST").ToArray();
        Assert.Equal(3, generated.Length);
        foreach (AiWireRequest request in generated)
        {
            JsonElement input = JsonSerializer.Deserialize<JsonElement>(request.Body).GetProperty("input");
            bool specified = input.GetProperty("text").GetString() is "explicit";
            foreach (string name in new[] { "instruction", "volume", "pitch", "enable_ssml" })
            {
                Assert.Equal(specified, input.TryGetProperty(name, out _));
            }
            if (specified)
            {
                Assert.Equal("calm", input.GetProperty("instruction").GetString());
                Assert.Equal(0, input.GetProperty("volume").GetInt32());
                Assert.Equal(1.2, input.GetProperty("pitch").GetDouble());
                Assert.False(input.GetProperty("enable_ssml").GetBoolean());
            }
        }
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(audio, await File.ReadAllBytesAsync(Path.Combine(files.Root, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav")).ConfigureAwait(true));
        }
        Assert.False(File.Exists(Path.Combine(files.Root, "unused.wav")));
    }
}
