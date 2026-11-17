using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>再送を延命しない保護と、時計補正後の新規WF受付を両立させます。</summary>
public sealed class WorkflowAdmissionClockTests
{
    /// <summary>対象の復帰を長く待った未配送の予定入力には、初回配送時にIDと受付期間をまとめて発行します。</summary>
    /// <returns>元の予定入力を保ち、古いIDの生成日時による拒否を避けることの確認です。</returns>
    [Fact]
    public async Task FirstScheduledDeliveryAfterLongWaitUsesAConsistentIdentityAndDeadlineAsync()
    {
        AdmissionTestClock clock = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        Contracts.Cluster.PeerInfo absent = new("target", Guid.NewGuid(), "target-host", "127.0.0.1", 12345,
            Contracts.Cluster.EPeerStatus.Unavailable, 0, null, clock.GetUtcNow());
        Runtime.Nodes.NodeExecutionContext context = Data.NodeTestContext.Create("{}", NodeValue.Null) with
        {
            Schedule = new("timeline", "source", Guid.NewGuid(), clock.GetUtcNow(), EScheduleMissedPolicy.Delay, TimeSpan.Zero),
            CapturedTargets = new Dictionary<string, Runtime.Workflows.Targets.TargetResolution>(StringComparer.Ordinal)
            { ["node"] = new(Contracts.Workflow.EExecutionScope.One, [absent]) },
        };
        Runtime.Scheduling.ScheduledTargetReadiness readiness = new(fixture.Peers, clock, TimeSpan.FromMinutes(5));
        Assert.True(readiness.Track(new(context, NodeResult.Success())));
        RequestAdmission original = RequestAdmission.Start(clock, TimeSpan.FromMinutes(5));
        RemoteExecutionRequest waiting = new(Guid.CreateVersion7(original.SourceNow), context.CausationId, context.WorkflowId, context.Revision,
            WorkflowFixture.Definition("node"), context.Inputs, absent.PeerId, absent.InstanceId, original.ExpiresAt) { Admission = original };
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(original.IsExpired);
        fixture.Peers.Others = [absent with { Status = Contracts.Cluster.EPeerStatus.Ready, InstanceId = Guid.NewGuid() }];
        Runtime.Scheduling.ScheduledTargetPreparation prepared = await readiness.PrepareAsync(waiting, () => false).ConfigureAwait(true);
        Assert.Null(prepared.Rejection);
        Assert.NotEqual(waiting.Id, prepared.Request.Id);
        Assert.Equal(waiting.CausationId, prepared.Request.CausationId);
        Assert.Equal(waiting.Node, prepared.Request.Node);
        Assert.Equal(fixture.Peers.Others[0].InstanceId, prepared.Request.TargetInstanceId);
        Assert.Equal(TimeSpan.FromMinutes(5), prepared.Request.Admission!.Remaining);
        ClusterRequest command = new(prepared.Request.Id, "node.execute", prepared.Request.TargetPeerId, prepared.Request.TargetInstanceId,
            prepared.Request.ExpiresAt, NodeValue.FromObject(prepared.Request)) { Admission = prepared.Request.Admission };
        Assert.True(RuntimeRequestIdentity.TryDeadline(command, clock.GetUtcNow(), out _));
    }

    /// <summary>Coordinatorの選定待ちでも、明示した利用可能なPeerへの通常ノード要求を止めません。</summary>
    /// <returns>Coordinatorの時計を問い合わせず配送できることの確認です。</returns>
    [Fact]
    public async Task ExplicitPeerExecutionDoesNotRequireACoordinatorClockAsync()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NoCoordinator peers = new(fixture.Peers);
        AdmissionTestClock clock = new();
        WorkflowEngine engine = new(fixture.Registry, peers, fixture.Dispatcher, clock);
        int executions = 0;
        fixture.Dispatcher.Handler = (request, _) =>
        {
            Assert.NotNull(request.Admission);
            Assert.False(request.Admission.IsExpired);
            Assert.Equal(peers.LocalInstanceId, request.TargetInstanceId);
            executions++;
            return Task.FromResult(NodeResult.Success());
        };
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("available", "Available", [WorkflowFixture.Definition("node") with
        { Target = new(Contracts.Workflow.EExecutionScope.One, [peers.LocalPeerId]) }], []));
        Assert.Null(peers.CoordinatorId);
        Assert.Equal(EOutcome.Succeeded, (await engine.ExecuteAsync(workflow, "node").ConfigureAwait(true)).Outcome);
        Assert.Equal(1, executions);
    }

    /// <summary>時計を戻した後の新規ノードは受け付け、古い要求を同じIDで復活させません。</summary>
    /// <returns>実WFの要求生成から、通信時計とUUIDの受付境界までの確認です。</returns>
    [Fact]
    public async Task NewWorkflowAfterRollbackKeepsItsOwnAdmissionBudgetAsync()
    {
        AdmissionTestClock source = new(), target = new();
        target.Adjust(TimeSpan.FromHours(2));
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        RequestClockClient client = new(source);
        RequestAdmissionClock server = new(fixture.Peers.LocalInstanceId, target);
        Task<ClusterResponse> ProbeAsync(ClusterRequest _, CancellationToken token)
            => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(server.Capture())));
        ClusterRequest old = new(Guid.CreateVersion7(source.GetUtcNow()), "node.execute", fixture.Peers.LocalPeerId,
            fixture.Peers.LocalInstanceId, source.GetUtcNow().AddSeconds(5), NodeValue.Null);
        ClockedClusterRequest original = (await client.PrepareAsync(old, ProbeAsync).ConfigureAwait(true))!;
        source.Advance(TimeSpan.FromSeconds(6));
        target.Advance(TimeSpan.FromSeconds(6));
        source.Adjust(TimeSpan.FromHours(-1));
        ClusterRequest? generated = null;
        ClockedClusterRequest? sent = null;
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            generated = new(request.Id, "node.execute", request.TargetPeerId, request.TargetInstanceId,
                request.ExpiresAt, NodeValue.FromObject(request)) { Admission = request.Admission };
            sent = await client.PrepareAsync(generated, ProbeAsync, token).ConfigureAwait(false);
            ClusterRequest received = server.Accept(sent!);
            Assert.False(received.Admission!.IsExpired);
            Assert.Equal(TimeSpan.FromMinutes(5), received.Admission.Remaining);
            Assert.True(RuntimeRequestIdentity.TryDeadline(received, target.GetUtcNow(), out _));
            return NodeResult.Success();
        };
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, source);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("clock", "Clock", [WorkflowFixture.Definition("node")], []));
        Assert.Equal(EOutcome.Succeeded, (await engine.ExecuteAsync(workflow, "node").ConfigureAwait(true)).Outcome);
        Assert.NotNull(generated);
        Assert.NotNull(sent);
        Assert.True(server.Accept((await client.PrepareAsync(old, ProbeAsync).ConfigureAwait(true))!).Admission!.IsExpired);
        Assert.Equal(original.StartByTicks, (await client.PrepareAsync(old, ProbeAsync).ConfigureAwait(true))!.StartByTicks);
        source.Adjust(TimeSpan.FromHours(3));
        source.Advance(TimeSpan.FromMinutes(5));
        target.Advance(TimeSpan.FromMinutes(5));
        ClockedClusterRequest retry = (await client.PrepareAsync(generated, ProbeAsync).ConfigureAwait(true))!;
        Assert.Equal(sent.StartByTicks, retry.StartByTicks);
        Assert.True(server.Accept(retry).Admission!.IsExpired);
    }

    /// <summary>参加観測を保ったまま、Coordinatorの選定待ちだけを再現します。</summary>
    private sealed class NoCoordinator(Runtime.Cluster.IPeerDirectory source) : Runtime.Cluster.IPeerDirectory
    {
        /// <summary>実行するPeerです。</summary>
        public string LocalPeerId => source.LocalPeerId;

        /// <summary>同じ起動実体を維持します。</summary>
        public Guid LocalInstanceId => source.LocalInstanceId;

        /// <summary>役割の担当だけが未確認です。</summary>
        public string? CoordinatorId => null;

        /// <summary>到達可能なPeerを返します。</summary>
        /// <returns>元の参加状態です。</returns>
        public IReadOnlyList<Contracts.Cluster.PeerInfo> GetPeers() => source.GetPeers();

        /// <summary>既存のHost Groupを維持します。</summary>
        /// <returns>設定された所属です。</returns>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups() => source.GetHostGroups();
    }
}
