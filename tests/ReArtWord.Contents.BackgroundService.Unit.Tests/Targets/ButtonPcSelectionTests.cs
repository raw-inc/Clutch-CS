using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>ボタンの各値ポートでPC候補を保持し、押した一回の実行先へ渡します。</summary>
public sealed class ButtonPcSelectionTests
{
    /// <summary>単一PC・複数PC・全PCの候補は、通常値と別の口で押下時だけ渡します。</summary>
    /// <returns>未押下、保存時評価、押下後の候補と実配送先の確認です。</returns>
    [Theory]
    [InlineData("value", "{\"mode\":\"selected\",\"hostIds\":[\"host-b\"]}", 1)]
    [InlineData("value2", "{\"mode\":\"selected\",\"hostIds\":[\"host-a\",\"host-b\"]}", 2)]
    [InlineData("value64", "{\"mode\":\"all\"}", 2)]
    public async Task CandidatesReachSelectedPcsOnlyOnPressAsync(string port, string settings, int count)
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ConcurrentQueue<RemoteExecutionRequest> received = new();
        string sink = "sink";
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                received.Enqueue(request);
                return Task.FromResult(NodeResult.Success(request.Inputs.GetValueOrDefault("value")));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        string payload = port switch { "value" => "value2", _ => "value" };
        WorkflowDefinition definition = new("button-pcs", "Button PCs", [
            WorkflowFixture.Definition("pc", PcSelectionPorts.SelectNode, settings),
            WorkflowFixture.Definition("button", ButtonValuePorts.NodeType) with { Inputs = new Dictionary<string, NodeValue> { [payload] = NodeValue.FromObject("payload") } },
            WorkflowFixture.Definition("sink") with { PcExecution = EPcExecutionMode.All }],
            [new("pc", "value", "button", port), new("button", port, "sink", PcSelectionPorts.Execution),
                new("button", payload, "sink", "value"), new("button", "start", "sink", "trigger")]);
        CompiledWorkflow workflow = Compile(fixture, definition);
        Assert.False(workflow.ConfigurationValues!.ContainsKey("button"));
        Assert.False(workflow.PcSelections!.ContainsKey("sink"));
        _ = await fixture.Engine.ExecuteAsync(workflow).ConfigureAwait(true);
        Assert.Empty(received);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(workflow, "button").ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal(count, received.Count);
        Assert.Equal(count, received.Select(request => request.TargetPeerId).Distinct().Count());
        Assert.All(received, request => Assert.Equal("payload", request.Inputs["value"].GetString()));
        Assert.Equal(count, report.Nodes.Single(node => node.NodeId is "sink").PcSelection!.CandidateHostIds.Count);
        if (count == 1)
        {
            Assert.Equal("b", Assert.Single(received).TargetPeerId);
        }
        NodeValue candidate = Assert.Single(report.Nodes.Single(node => node.NodeId is "button").Targets).Result.Outputs[port];
        Assert.Single(PcSelectionValue.Read(candidate));
        _ = await fixture.Engine.ExecuteAsync(workflow).ConfigureAwait(true);
        Assert.Equal(count, received.Count);
    }

    /// <summary>遅い押下と別の押下が合流しても、PC候補と同じ押下の値を混ぜません。</summary>
    /// <returns>保持値を有効にした二つの同時要求と、次の未押下実行の確認です。</returns>
    [Fact]
    public async Task OverlappingButtonsKeepTheirOwnPcAndPayloadThroughMergeAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(string Peer, string Payload)> received = new();
        string sink = "sink", peerA = "a";
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                if (string.Equals(request.TargetPeerId, peerA, StringComparison.Ordinal))
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }
                received.Enqueue((request.TargetPeerId, request.Inputs["value"].GetString()!));
                return NodeResult.Success(request.Inputs.GetValueOrDefault("value"));
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        WorkflowDefinition definition = new("button-merge-pcs", "Button merge PCs", [
            WorkflowFixture.Definition("pcA", PcSelectionPorts.SelectNode, """{"mode":"selected","hostIds":["host-a"]}"""),
            WorkflowFixture.Definition("pcB", PcSelectionPorts.SelectNode, """{"mode":"selected","hostIds":["host-b"]}"""),
            WorkflowFixture.Definition("buttonA", ButtonValuePorts.NodeType) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("A") } },
            WorkflowFixture.Definition("buttonB", ButtonValuePorts.NodeType) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("B") } },
            WorkflowFixture.Definition("pcs", ValueMergePorts.NodeType), WorkflowFixture.Definition("payload", ValueMergePorts.NodeType), WorkflowFixture.Definition("sink")],
            [new("pcA", "value", "buttonA", "value2"), new("pcB", "value", "buttonB", "value2"),
                new("buttonA", "value2", "pcs", "input1"), new("buttonB", "value2", "pcs", "input2"),
                new("buttonA", "value", "payload", "input1"), new("buttonB", "value", "payload", "input2"),
                new("pcs", "value", "sink", PcSelectionPorts.Execution), new("pcs", "start", "sink", "trigger"), new("payload", "value", "sink", "value")]);
        CompiledWorkflow workflow = Compile(fixture, definition);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(workflow, "buttonA", cancellationToken: timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(workflow, "buttonB", cancellationToken: timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.False(first.IsCompleted);
            Assert.Equal(("b", "B"), Assert.Single(received));
        }
        finally
        {
            release.TrySetResult();
            await first.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        Assert.Equal(new[] { ("b", "B"), ("a", "A") }, received);
        _ = await fixture.Engine.ExecuteAsync(workflow).ConfigureAwait(true);
        Assert.Equal(2, received.Count);
    }

    /// <summary>PC選択・ボタン・合流は実実装を使い、二台目の参加状態を用意します。</summary>
    /// <returns>保持値も有効にした実行器です。</returns>
    private static WorkflowFixture Fixture()
    {
        WorkflowFixture fixture = new(new PcSelectionNode(), new ButtonNode(), new ValueMergeNode(),
            new WorkflowFixture.ActionNode((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs.GetValueOrDefault("value")))));
        fixture.Peers.Others = [new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 1, EPeerStatus.Ready, 900, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        fixture.Engine.RetainedValues = new TestStateStore();
        return fixture;
    }

    /// <summary>編集時と同じ保存契約で型検証と実行計画の構築を行います。</summary>
    /// <returns>問題のない保存版の計画です。</returns>
    private static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        return Assert.IsType<CompiledWorkflow>(compiled.Workflow);
    }
}
