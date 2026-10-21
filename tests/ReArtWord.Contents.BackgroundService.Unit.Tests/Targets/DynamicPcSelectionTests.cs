using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>If-Startで選んだPC候補の先着を、要求ごとの宛先へ固定します。</summary>
public sealed class DynamicPcSelectionTests
{
    /// <summary>開始だけを別経路の合流へ通しても、同じ要求が選んだPCを待って採用します。</summary>
    /// <returns>並行要求が異なるPCを選び、宛先や要求値を混ぜない確認です。</returns>
    [Fact]
    public async Task SignalMergeKeepsTheRequestsDynamicPcChoiceAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ConcurrentQueue<(bool Choice, string Peer)> observed = new();
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            string sink = "sink";
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                observed.Enqueue((request.Inputs["value"].Json.GetBoolean(), request.TargetPeerId));
                return NodeResult.Success();
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        WorkflowDefinition definition = Definition();
        definition = definition with
        {
            Nodes = [.. definition.Nodes.Select(node => node with { Inputs = null }), WorkflowFixture.Definition("signal", "control.signal.each")],
            Wires = [.. definition.Wires, new("source", CompletionPorts.Signal, "signal", "input1"),
                new("signal", "start", "sink", "trigger"), new("source", "value", "sink", "value")],
        };
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        bool[] choices = [true, false, true, false];
        ExecutionReport[] reports = await Task.WhenAll(choices.Select(choice => fixture.Engine.ExecuteAsync(workflow, "source",
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(choice) }))).ConfigureAwait(true);
        Assert.All(reports, report => Assert.Equal(EOutcome.Succeeded, report.Outcome));
        Assert.Equal(choices.Length, observed.Count);
        Assert.All(observed, item => Assert.Equal(item.Choice, item.Peer is "a"));
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>前回や並行要求のPC候補を保持値から流用しません。</summary>
    /// <returns>交互・同時に起動する要求と、分岐した宛先の一致です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IfStartSelectsPcsIndependentlyForEveryRequestAsync(bool combinedIf)
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ConcurrentDictionary<Guid, string> destinations = new();
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            string sink = "sink";
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                Assert.True(destinations.TryAdd(request.CausationId, request.TargetPeerId));
                await Task.Delay(15, token).ConfigureAwait(false);
                return NodeResult.Success(NodeValue.FromObject(request.TargetPeerId));
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        WorkflowDefinition definition = Definition();
        if (combinedIf)
        {
            string no = "no", yes = "yes";
            definition = definition with
            {
                Nodes = definition.Nodes.Where(node => !string.Equals(node.Id, no, StringComparison.Ordinal)).Select(node =>
                {
                    if (string.Equals(node.Id, yes, StringComparison.Ordinal))
                    {
                        return node with { Type = "control.if" };
                    }
                    return node;
                }).ToArray(),
                Wires = definition.Wires.Where(wire => !string.Equals(wire.ToNode, no, StringComparison.Ordinal)).Select(wire => wire.FromNode switch
                {
                    "yes" => wire with { FromPort = "trueStart" }, "no" => wire with { FromNode = yes, FromPort = "falseStart" }, _ => wire,
                }).ToArray(),
            };
        }
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        Assert.Contains(workflow.Incoming["sink"], wire => string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal));
        Assert.False(workflow.ConfigurationValues!.ContainsKey("pcA"));
        Assert.False(workflow.ConfigurationValues.ContainsKey("merge"));
        bool[] choices = [true, false, true, false, false, true];
        ExecutionReport[] reports = await Task.WhenAll(choices.Select(choice => fixture.Engine.ExecuteAsync(workflow, "source",
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(choice) }))).ConfigureAwait(true);
        for (int index = 0; index < reports.Length; index++)
        {
            Assert.Equal(EOutcome.Succeeded, reports[index].Outcome);
            string expected = "b";
            if (choices[index])
            {
                expected = "a";
            }
            Assert.Equal(expected, destinations[reports[index].Id]);
            Assert.Equal("host-" + expected, Assert.Single(reports[index].Nodes.Single(node => node.NodeId is "sink").PcSelection!.CandidateHostIds));
        }
    }

    /// <summary>遅い入力1を待たず入力2で開始し、後着は別要求として現在の宛先を変えません。</summary>
    /// <returns>通常ValueMergeと同じ到着分離と、開始済み処理の宛先固定です。</returns>
    [Fact]
    public async Task FirstArrivingCandidatesDoNotWaitOrMixWithLateCandidatesAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        TaskCompletionSource releaseA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enteredB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enteredA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(Guid Request, string Peer)> destinations = new();
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            string pcA = "pcA", sink = "sink", peerB = "b";
            if (string.Equals(request.Node.Id, pcA, StringComparison.Ordinal))
            {
                await releaseA.Task.WaitAsync(token).ConfigureAwait(false);
            }
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                destinations.Enqueue((request.CausationId, request.TargetPeerId));
                if (string.Equals(request.TargetPeerId, peerB, StringComparison.Ordinal))
                {
                    enteredB.TrySetResult();
                }
                else
                {
                    enteredA.TrySetResult();
                }
                await finish.Task.WaitAsync(token).ConfigureAwait(false);
                return NodeResult.Success(NodeValue.FromObject(request.TargetPeerId));
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        WorkflowDefinition definition = Definition();
        string secondGate = "no";
        definition = definition with { Nodes = definition.Nodes.Select(node => string.Equals(node.Id, secondGate, StringComparison.Ordinal) ? node with { Type = "control.ifStart" }
                                                                                                                                            : node).ToArray() };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source",
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(true) }, cancellationToken: timeout.Token);
        try
        {
            await enteredB.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal("b", Assert.Single(destinations).Peer);
            releaseA.TrySetResult();
            await enteredA.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(["b", "a"], destinations.Select(item => item.Peer));
            Assert.Equal(2, destinations.Select(item => item.Request).Distinct().Count());
        }
        finally
        {
            releaseA.TrySetResult();
            finish.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
    }

    /// <summary>次の要求で両分岐が閉じた場合、前回のPC候補を再使用しません。</summary>
    /// <returns>以前の成功後でも未到着のPC候補では処理しない確認です。</returns>
    [Fact]
    public async Task MissingSignalsNeverReusePreviousPcCandidatesAsync()
    {
        WorkflowFixture fixture = Fixture();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        int calls = 0;
        fixture.Dispatcher.Handler = (request, token) =>
        {
            string sink = "sink";
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(NodeResult.Success());
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition definition = Definition();
        string second = "no";
        definition = definition with { Nodes = definition.Nodes.Select(node =>
        {
            if (string.Equals(node.Id, second, StringComparison.Ordinal))
            {
                return node with { Type = "control.ifStart" };
            }
            return node;
        }).ToArray() };
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        _ = await fixture.Engine.ExecuteAsync(workflow, "source", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        Assert.Equal(2, calls);
        ExecutionReport empty = await fixture.Engine.ExecuteAsync(workflow, "source", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(false) }).ConfigureAwait(true);
        Assert.Equal(2, calls);
        Assert.Equal(EOutcome.NotExecuted, empty.Nodes.Single(node => node.NodeId is "sink").Outcome);
    }

    /// <summary>条件とPC値の生成は実ノードを使い、最終処理だけで宛先を観測します。</summary>
    /// <returns>二つの参加PCを持つ実エンジンです。</returns>
    private static WorkflowFixture Fixture()
    {
        WorkflowFixture fixture = new(new PcSelectionNode(), new IfNode(), new IfNode(true), new IfElseNode(), new ValueMergeNode(), new SignalMergeNode("each", new State.TestStateStore()),
            new ValueMergeFixture.Probe((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs.GetValueOrDefault("value")))));
        fixture.Peers.Others = [new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 1, EPeerStatus.Ready, 900, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        return fixture;
    }

    /// <summary>利用者が指定したIf-Start→PC選択→ValueMerge→処理の接続です。</summary>
    /// <returns>実行開始時までPCを選ばない定義です。</returns>
    internal static WorkflowDefinition Definition() => new("dynamic-pc", "Dynamic PC selection", [
        new("source", "test.value", NodeSettings.Schema("{}")), new("yes", "control.ifStart", NodeSettings.Schema("{}")), new("no", "control.ifNotStart", NodeSettings.Schema("{}")),
        new("pcA", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-a"]}""")),
        new("pcB", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-b"]}""")),
        new("merge", ValueMergePorts.NodeType, NodeSettings.Schema("{}")),
        new("sink", "test.value", NodeSettings.Schema("{}"), new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("payload") })],
        [new("source", "value", "yes", "condition"), new("source", "value", "no", "condition"),
            new("yes", "start", "pcA", "trigger"), new("no", "start", "pcB", "trigger"),
            new("pcA", "value", "merge", "input1"), new("pcB", "value", "merge", "input2"), new("merge", "value", "sink", PcSelectionPorts.Execution)]);
}
