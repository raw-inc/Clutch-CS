using System.Collections.Concurrent;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>所有範囲を実行の子経路と次の受信へ引き継ぎ、他の範囲から分離します。</summary>
public sealed class SourceScopeTests
{
    /// <summary>値合流が作る子実行とSourceの次受信をまたいでも、元の範囲だけを停止します。</summary>
    /// <returns>独立した受信IDと同じ所有範囲、別範囲の継続確認です。</returns>
    [Fact]
    public async Task MergeAndSubsequentReceptionKeepOwnershipWithoutStoppingAnotherScopeAsync()
    {
        ProbeSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new ValueMergeNode(), new WorkflowFixture.ActionNode((context, _) =>
        {
            Assert.True(observed.Writer.TryWrite(context));
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        ScopeControl scopes = new();
        fixture.Sources.Scopes = scopes;
        fixture.Engine.ScopedDispatchCheck = (_, _) => ValueTask.FromResult<NodeResult?>(null);
        WorkflowDefinition definition = new("w", "Scoped source", [
            WorkflowFixture.Definition("seed", "test.source"), WorkflowFixture.Definition("merge", ValueMergePorts.NodeType),
            WorkflowFixture.Definition("listener", "test.source"), WorkflowFixture.Definition("other", "test.source"), WorkflowFixture.Definition("sink")],
            [new("seed", "value", "merge", "input1"), new("merge", "start", "listener", "trigger"), new("listener", "value", "sink", "value")]);
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, definition);
        await fixture.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        SourceScope scope = scopes.Create(definition.Id), other = scopes.Create(definition.Id);
        RemoteExecutionRequest request = Request(fixture, plan, "other", other);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Sources.StartAsync(request, timeout.Token).ConfigureAwait(true)).Outcome);
        NodeExecutionContext initial = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, plan.Revision, "seed", "a", definition.Nodes[0].Settings,
            new Dictionary<string, NodeValue>(), TimeProvider.System) { SourceScopes = [scope] };
        ExecutionReport result = await fixture.Engine.ExecuteEmissionAsync(plan, new(initial, NodeResult.Success(NodeValue.FromObject("fallback"))), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(scope, Assert.Single(source.Started["listener"].Initial.SourceScopes!));
        NodeSourceContext listener = source.Started["listener"];
        NodeExecutionContext received = listener.BeginItem();
        await listener.EmitAsync(received, NodeResult.Success(NodeValue.FromObject("payload")), timeout.Token).ConfigureAwait(true);
        NodeExecutionContext downstream = await observed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(scope, Assert.Single(downstream.SourceScopes!));
        Assert.NotEqual(initial.CausationId, downstream.CausationId);
        Assert.True(await fixture.Sources.EndScopeAsync(scope, timeout.Token).ConfigureAwait(true));
        Assert.Equal(ESourceState.Completed, fixture.Sources.GetSources().Single(item => item.NodeId is "listener").Status.State);
        Assert.Equal(ESourceState.Listening, fixture.Sources.GetSources().Single(item => item.NodeId is "other").Status.State);
        scopes.Closed.Add(scope);
        NodeResult refused = await fixture.Sources.StartAsync(Request(fixture, plan, "listener", scope), timeout.Token).ConfigureAwait(true);
        Assert.Equal("source.scope.closed", refused.ReasonCode);
        Assert.Equal(ESourceState.Completed, fixture.Sources.GetSources().First(item => item.NodeId is "listener").Status.State);
        Assert.Empty(fixture.Sources.Failures);
    }

    /// <summary>生存信号の配線があっても自動起動し、到着時は同じ監視へ通知だけを渡します。</summary>
    /// <returns>再起動や下流の誤発火がない確認です。</returns>
    [Fact]
    public async Task WiredHeartbeatAutomaticallyStartsAndResetsTheSameMonitorAsync()
    {
        ProbeSource source = new();
        int downstream = 0;
        WorkflowFixture fixture = new(source, new SignalWatchdogNode(), new WorkflowFixture.ActionNode((_, _) =>
        {
            Interlocked.Increment(ref downstream);
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        fixture.Sources.Scopes = new ScopeControl();
        WorkflowDefinition definition = new("w", "Heartbeat", [WorkflowFixture.Definition("main", "test.source"),
            WorkflowFixture.Definition("watch", "control.signalWatchdog", "{\"timeoutMs\":60000}") with { StartOnLoad = true }, WorkflowFixture.Definition("sink")],
            [new("main", "start", "watch", "trigger"), new("watch", "start", "sink", "trigger")], Enabled: true);
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, definition);
        await fixture.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        SourceExecutionInfo before = Assert.Single(fixture.Sources.GetSources());
        Assert.Equal("watch", before.NodeId);
        NodeExecutionContext initial = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, plan.Revision, "main", "a", definition.Nodes[0].Settings,
            new Dictionary<string, NodeValue>(), TimeProvider.System);
        for (int index = 0; index < 3; index++)
        {
            initial = initial with { CausationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid() };
            ExecutionReport report = await fixture.Engine.ExecuteEmissionAsync(plan, new(initial, NodeResult.Success()), timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Nodes.Single(item => item.NodeId is "watch").Outcome);
            Assert.Equal(before.Id, Assert.Single(fixture.Sources.GetSources()).Id);
        }
        Assert.Equal(0, downstream);
    }

    /// <summary>所有情報に対応しない実行先では、後続の副作用を配送前に拒否します。</summary>
    /// <returns>Capability不明を通常開始へ読み替えない確認です。</returns>
    [Fact]
    public async Task MissingScopeCapabilityPreventsDownstreamDispatchAsync()
    {
        int called = 0;
        WorkflowFixture fixture = new(new ProbeSource(), new WorkflowFixture.ActionNode((_, _) =>
        {
            called++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("w", "Old target", [WorkflowFixture.Definition("source", "test.source"), WorkflowFixture.Definition("sink")],
            [new("source", "start", "sink", "trigger")]);
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, definition);
        NodeExecutionContext initial = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, plan.Revision, "source", "a", definition.Nodes[0].Settings,
            new Dictionary<string, NodeValue>(), TimeProvider.System) { SourceScopes = [new(Guid.NewGuid(), "w", "a", Guid.NewGuid())] };
        ExecutionReport report = await fixture.Engine.ExecuteEmissionAsync(plan, new(initial, NodeResult.Success())).ConfigureAwait(true);
        Assert.Equal("source.scope.unavailable", Assert.Single(report.Nodes.Single(item => item.NodeId is "sink").Targets).Result.ReasonCode);
        Assert.Equal(0, called);
    }

    /// <summary>所有元が遅延していても、他WFの設定反映や再調停を待たせません。</summary>
    /// <returns>有限の背景照会と、終了確認後の待受回収です。</returns>
    [Fact]
    public async Task SlowOwnerInspectionDoesNotBlockReconciliationAndClosesAfterConfirmationAsync()
    {
        WorkflowFixture fixture = new(new ProbeSource());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        ScopeControl control = new() { ReadResult = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        fixture.Sources.Scopes = control;
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, new("w", "Slow owner", [WorkflowFixture.Definition("listener", "test.source")], []));
        await fixture.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        SourceScope scope = control.Create(plan.Definition.Id);
        _ = await fixture.Sources.StartAsync(Request(fixture, plan, "listener", scope), timeout.Token).ConfigureAwait(true);
        await fixture.Sources.ReconcileAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(true);
        await control.ReadStarted.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        for (int index = 0; index < 100; index++)
        {
            await fixture.Sources.ReconcileAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(true);
        }
        await fixture.Sources.ApplyAsync(plan, timeout.Token).WaitAsync(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(true);
        Assert.False(control.ReadResult.Task.IsCompleted);
        Assert.Equal(1, control.ReadCount);
        control.ReadResult.SetResult(false);
        while (fixture.Sources.GetSources().Single().Status.State != ESourceState.Completed)
        {
            await fixture.Sources.ReconcileAsync(timeout.Token).ConfigureAwait(true);
            await Task.Delay(1, timeout.Token).ConfigureAwait(true);
        }
        Assert.Empty(fixture.Sources.Failures);
    }

    /// <summary>新しい実行IDを生成する開始信号合流も、親の所有範囲を引き継ぎます。</summary>
    /// <returns>子実行の識別子と、維持された所有範囲です。</returns>
    [Fact]
    public async Task SignalMergeChildInheritsItsParentScopeAsync()
    {
        NodeExecutionContext? observed = null;
        SignalMergeNode merge = new("each", new State.TestStateStore());
        WorkflowFixture fixture = new(new ProbeSource(), merge, new WorkflowFixture.ActionNode((context, _) =>
        {
            observed = context;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Engine.ScopedDispatchCheck = (_, _) => ValueTask.FromResult<NodeResult?>(null);
        WorkflowDefinition definition = new("w", "Child scope", [WorkflowFixture.Definition("source", "test.source"),
            WorkflowFixture.Definition("merge", merge.Descriptor.Type), WorkflowFixture.Definition("sink")],
            [new("source", "start", "merge", "input1"), new("merge", "start", "sink", "trigger")]);
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, definition);
        SourceScope scope = new(Guid.NewGuid(), "w", "a", Guid.NewGuid());
        NodeExecutionContext initial = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, plan.Revision, "source", "a", definition.Nodes[0].Settings,
            new Dictionary<string, NodeValue>(), TimeProvider.System) { SourceScopes = [scope] };
        _ = await fixture.Engine.ExecuteEmissionAsync(plan, new(initial, NodeResult.Success())).ConfigureAwait(true);
        Assert.NotNull(observed);
        Assert.NotEqual(initial.CausationId, observed.CausationId);
        Assert.Equal(scope, Assert.Single(observed.SourceScopes!));
    }

    /// <summary>開始許可の取得直後に復帰しても、実Taskの登録前という隙間で待受を取り残しません。</summary>
    /// <returns>同じ開始受付境界を待ってから停止する確認です。</returns>
    [Fact]
    public async Task RecoveryDuringSynchronousSourceStartupCannotMissTheNewListenerAsync()
    {
        using ManualResetEventSlim hold = new(false);
        ProbeSource source = new() { StartHold = hold };
        WorkflowFixture fixture = new(source);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        ScopeControl control = new();
        fixture.Sources.Scopes = control;
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, new("w", "Startup race", [WorkflowFixture.Definition("listener", "test.source")], []));
        await fixture.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        SourceScope scope = control.Create(plan.Definition.Id);
        string listener = "listener";
        Task<NodeResult> starting = Task.Run(() => fixture.Sources.StartAsync(Request(fixture, plan, listener, scope), timeout.Token), timeout.Token);
        try
        {
            while (!source.Started.ContainsKey(listener))
            {
                await Task.Delay(1, timeout.Token).ConfigureAwait(true);
            }
            control.Closed.Add(scope);
            Task<bool> ending = fixture.Sources.EndScopeAsync(scope, timeout.Token).AsTask();
            hold.Set();
            Assert.True(await ending.ConfigureAwait(true));
            _ = await starting.ConfigureAwait(true);
            Assert.Equal(ESourceState.Completed, Assert.Single(fixture.Sources.GetSources()).Status.State);
        }
        finally
        {
            hold.Set();
            _ = await starting.ConfigureAwait(true);
        }
    }

    /// <summary>一つの開始にだけ所有情報を付けます。</summary>
    /// <returns>ローカルの実際の受付経路へ渡す要求です。</returns>
    private static RemoteExecutionRequest Request(WorkflowFixture fixture, CompiledWorkflow plan, string node, SourceScope scope)
        => new(Guid.NewGuid(), Guid.NewGuid(), plan.Definition.Id, plan.Revision, plan.FindNode(node)!, new Dictionary<string, NodeValue>(),
            fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1)) { SourceScopes = [scope] };

    /// <summary>入力本体を試験から通知し、通常停止にも反応する実Sourceです。</summary>
    private sealed class ProbeSource : ISourceNode
    {
        internal ManualResetEventSlim? StartHold
        {
            get; init;
        }

        internal ConcurrentDictionary<string, NodeSourceContext> Started
        {
            get;
        } = new(StringComparer.Ordinal);

        public NodeDescriptor Descriptor => NodeSupport.Describe("test.source", "Source", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>外部設定に依存しません。</summary>
        /// <returns>追加制約なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>監視された寿命が閉じるまで待受を維持します。</summary>
        /// <returns>停止時は共通ランタイムへ制御を返します。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            Started[context.Initial.NodeId] = context;
            StartHold?.Wait(cancellationToken);
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
            await context.ReportStatusAsync(new(ESourceState.Listening), lifetime.Token).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
            return NodeResult.Success();
        }
    }

    /// <summary>遠隔制御を省き、開閉状態だけを試験が指定します。</summary>
    private sealed class ScopeControl : ISourceScopeControl
    {
        internal ConcurrentBag<SourceScope> Closed
        {
            get;
        } = [];

        internal TaskCompletionSource<bool?>? ReadResult
        {
            get; init;
        }

        internal TaskCompletionSource ReadStarted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadCount;

        /// <summary>一回の待受範囲を作成します。</summary>
        /// <returns>独立した範囲です。</returns>
        public SourceScope Create(string workflowId) => new(Guid.NewGuid(), workflowId, "a", Guid.NewGuid());

        /// <summary>明示した終了後の登録を拒否します。</summary>
        /// <returns>現在の試験条件です。</returns>
        public async ValueTask<bool?> CheckAsync(SourceScope scope, bool enroll, CancellationToken cancellationToken = default)
        {
            if (!enroll && ReadResult is not null)
            {
                Interlocked.Increment(ref ReadCount);
                ReadStarted.TrySetResult();
                return await ReadResult.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return !Closed.Contains(scope);
        }

        /// <summary>終了はSupervisorから直接検証します。</summary>
        /// <returns>追加処理なしです。</returns>
        public ValueTask<NodeResult> EndAsync(SourceScope scope, CancellationToken cancellationToken = default) => ValueTask.FromResult(NodeResult.Success());
    }
}
