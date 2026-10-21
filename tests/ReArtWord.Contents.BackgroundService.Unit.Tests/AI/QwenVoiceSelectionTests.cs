using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>反復内の抽選結果がKey-Valueの分離と設定入力を経てQwenのHTTP本文へ届くことを確認します。</summary>
public sealed class QwenVoiceSelectionTests
{
    /// <summary>開始Signalでも0始まりの添字でも各要素を抽選し、別の反復やWF実行の声を再利用しません。</summary>
    /// <returns>実ランタイムとローカルHTTPを通した、逐次・並列反復の検証です。</returns>
    [Theory]
    [InlineData(false, "start", false, false)]
    [InlineData(false, "index", false, false)]
    [InlineData(true, "start", false, false)]
    [InlineData(true, "index", false, false)]
    [InlineData(false, "index", true, false)]
    [InlineData(true, "index", true, false)]
    [InlineData(false, "index", true, true)]
    [InlineData(true, "index", true, true)]
    public async Task EachElementSendsItsSelectedVoiceAsync(bool parallel, string triggerPort, bool mergedStart, bool mergedVoice)
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
        WorkflowFixture fixture = new(new ButtonNode(), new CommentNode(), new RandomPickNode(), new KeyValuePartNode(false),
            new ForEachNode(parallel), new ForEachEndNode(), new SignalMergeNode("each", new TestStateStore()), new ValueMergeNode(), qwen,
            new WorkflowFixture.ActionNode((context, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(Path.Combine(files.Root,
                context.Inputs["value"].Json.GetInt32().ToString(CultureInfo.InvariantCulture) + ".wav"))))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);

        // 抽選結果だけを決定的にし、偶然の連続当選に依存せず実行回数と下流の設定反映を検証します。
        int draws = 0;
        string drawId = "draw";
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, drawId, StringComparison.Ordinal))
            {
                int index = Interlocked.Increment(ref draws) - 1;
                return Task.FromResult(NodeResult.Success(DictionaryValues.Select(request.Inputs["value"], index % 6)));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        JsonElement empty = NodeSettings.Schema("{}");
        JsonElement settings = NodeSettings.Schema(AiGeneratorTests.Settings(server, new Dictionary<string, object>
        {
            ["model"] = "qwen-audio-3.0-tts-flash", ["voice"] = "must-be-overridden", ["workspaceId"] = "fixture-workspace",
            ["outputPath"] = Path.Combine(files.Root, "unused.wav"),
        }));
        string loopType = IterationNodeTypes.ForEach;
        if (parallel)
        {
            loopType = IterationNodeTypes.ParallelForEach;
        }
        NodeValue items = NodeValue.FromJson(JsonSerializer.SerializeToElement(Enumerable.Range(0, 12).Select(index => new { text = index.ToString(CultureInfo.InvariantCulture) })));
        List<WireDefinition> wires = [new("button", "value", "candidates", "value"), new("candidates", "value", "draw", "value"),
            new("loop", triggerPort, "draw", "trigger"), new("draw", "value", "voice", "value"), new("voice", "value", "qwen", "$settings.voice"),
            new("loop", "index", "path", "value"), new("path", "value", "qwen", "$settings.outputPath"), new("loop", "item/text", "qwen", "value"),
            new("loop", "loop", "end", "loop"), new("qwen", CompletionPorts.Success, "end", "value")];
        List<NodeDefinition> nodes = [new("button", "control.button", empty), new("candidates", "value.comment", empty),
            new("draw", "data.randomPick", empty), new("voice", "data.keyValue.value", empty),
            new("loop", loopType, empty, new Dictionary<string, NodeValue> { ["value"] = items }), WorkflowFixture.Definition("path"),
            new("qwen", qwen.Descriptor.Type, settings), new("end", IterationNodeTypes.End, empty)];
        if (mergedStart)
        {
            nodes.Add(new("merge", "control.signal.each", empty));
            wires.AddRange([new("button", "start", "merge", "input1"), new("merge", "start", "loop", "trigger")]);
        }
        else
        {
            wires.Add(new("button", "start", "loop", "trigger"));
        }
        if (mergedVoice)
        {
            nodes.Add(new("voiceMerge", ValueMergePorts.NodeType, empty));
            wires.RemoveAll(wire => wire.FromNode is "voice" && wire.ToNode is "qwen");
            wires.AddRange([new("voice", "value", "voiceMerge", "input1"), new("voiceMerge", "value", "qwen", "$settings.voice")]);
        }
        WorkflowDefinition definition = new("qwen-voices", "Qwen voices", nodes, wires);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, definition);
        Dictionary<string, string> voices = Enumerable.Range(0, 6).ToDictionary(index => "speaker-" + index.ToString(CultureInfo.InvariantCulture),
            index => "voice-" + index.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        for (int batch = 0; batch < 2; batch++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled, "button", new Dictionary<string, NodeValue>
            {
                ["value"] = NodeValue.FromJson(JsonSerializer.SerializeToElement(voices)),
            }).ConfigureAwait(true);
            Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report.Nodes.Select(node => new { node.NodeId, node.Outcome, Reasons = node.Targets.Select(target => target.Result.ReasonCode) })));
            Assert.Equal(12 * (batch + 1), draws);
            JsonElement[] inputs = server.Requests.Where(request => request.Method is "POST").Skip(batch * 12)
                .Select(request => JsonSerializer.Deserialize<JsonElement>(request.Body).GetProperty("input")).ToArray();
            Assert.Equal(12, inputs.Length);
            Assert.Equal(12, inputs.Select(input => input.GetProperty("text").GetString()).Distinct(StringComparer.Ordinal).Count());
            foreach (string voice in voices.Values)
            {
                Assert.Equal(2, inputs.Count(input => string.Equals(input.GetProperty("voice").GetString(), voice, StringComparison.Ordinal)));
            }
        }
        Assert.False(File.Exists(Path.Combine(files.Root, "unused.wav")));
    }
}
