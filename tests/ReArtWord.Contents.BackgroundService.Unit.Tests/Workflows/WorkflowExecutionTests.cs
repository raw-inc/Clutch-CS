using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>各ノードだけでは確認できない分岐・対象集約・安全停止を検証します。</summary>
public sealed class WorkflowExecutionTests
{
    /// <summary>選択した経路だけを実行し、無関係な根を巻き込みません。</summary>
    /// <returns>非同期の検証です。</returns>
    [Fact]
    public async Task SelectedRouteRunsOnlyItsDependentBranch()
    {
        ConcurrentBag<string> visited = [];
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((context, _) =>
        {
            visited.Add(context.NodeId);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(7), "false"));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("w", "branch",
            [WorkflowFixture.Definition("entry"), WorkflowFixture.Definition("yes"), WorkflowFixture.Definition("no"), WorkflowFixture.Definition("other")],
            [new WireDefinition("entry", "value", "yes", "value", "true"), new WireDefinition("entry", "value", "no", "value", "false")])).Workflow!;

        ExecutionReport result = await fixture.Engine.ExecuteAsync(plan, "entry");

        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(["entry", "no"], visited.Order(StringComparer.Ordinal));
        Assert.Equal(EOutcome.NotExecuted, result.Nodes.Single(node => string.Equals(node.NodeId, "yes", StringComparison.Ordinal)).Outcome);
    }

    /// <summary>Allの一部が失敗した場合、全体成功の配線へ値を流しません。</summary>
    /// <returns>非同期の検証です。</returns>
    [Fact]
    public async Task PartialTargetFailurePreservesPerPeerResultsAndBlocksSuccessWire()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(static (_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new PeerInfo("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, EPeerStatus.Ready, 99, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow)];
        fixture.Dispatcher.Handler = static (request, _) =>
        {
            string failedPeer = "b";
            if (string.Equals(request.TargetPeerId, failedPeer, StringComparison.Ordinal))
            {
                return Task.FromResult(NodeResult.Failure("fixture.failure", "意図した対象失敗"));
            }
            return Task.FromResult(NodeResult.Success(NodeValue.FromObject(1)));
        };
        NodeDefinition entry = WorkflowFixture.Definition("entry") with { Target = new TargetDefinition(EExecutionScope.All, ["a", "b"]) };
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("w", "partial", [entry, WorkflowFixture.Definition("child")], [new WireDefinition("entry", "value", "child", "value")])).Workflow!;

        ExecutionReport report = await fixture.Engine.ExecuteAsync(plan, "entry");

        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(2, report.Nodes[0].Targets.Count);
        Assert.Equal(EOutcome.NotExecuted, report.Nodes[1].Outcome);
    }

    /// <summary>候補内の一台を選ぶ場合も、今回の指示に従って優先度を使用します。</summary>
    /// <returns>非同期の検証です。</returns>
    [Fact]
    public async Task AnySelectionUsesHighestPriorityWithinCandidates()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(static (_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new PeerInfo("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, EPeerStatus.Ready, int.MaxValue, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow)];
        NodeDefinition node = WorkflowFixture.Definition("n") with { Target = new TargetDefinition(EExecutionScope.Any, ["b", "a"]) };

        TargetResolution result = new TargetResolver(fixture.Peers).Resolve(node, fixture.Registry.Find(node.Type)!.Descriptor);

        Assert.Equal("b", Assert.Single(result.Peers).PeerId);
    }

    /// <summary>通常ノードと安全指定ノードの単体時既定を区別します。</summary>
    /// <returns>非同期の検証です。</returns>
    [Fact]
    public async Task SingletonStopsOnlySafetyDesignatedNodes()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(static (_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition ordinary = WorkflowFixture.Definition("ordinary");
        NodeDefinition designated = ordinary with { Id = "designated", Safety = new NodeSafetyPolicy() };

        Assert.Null(await fixture.Safety.WaitAsync("w", ordinary));
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Safety.WaitAsync("w", designated))!.Outcome);
    }

    /// <summary>停止中のLatestは古い一件を未実行にし、再開時に最新だけを許します。</summary>
    /// <returns>非同期の検証です。</returns>
    [Fact]
    public async Task LatestInputReplacesOlderPendingInput()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(static (_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition node = WorkflowFixture.Definition("n") with { Safety = new NodeSafetyPolicy(false, Inputs: EStoppedInputPolicy.Latest) };
        fixture.Safety.SetStopped("w", "n", true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<NodeResult?> first = fixture.Safety.WaitAsync("w", node, timeout.Token).AsTask();
        Task<NodeResult?> latest = fixture.Safety.WaitAsync("w", node, timeout.Token).AsTask();

        Assert.Equal("safety.replaced", (await first.ConfigureAwait(true))!.ReasonCode);
        Assert.False(latest.IsCompleted);
        fixture.Safety.SetStopped("w", "n", false);
        Assert.Null(await latest.ConfigureAwait(true));
    }
}
