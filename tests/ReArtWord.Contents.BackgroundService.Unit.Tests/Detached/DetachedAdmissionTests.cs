using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Detached;

/// <summary>離脱・依存競合を受付時だけでなく実開始とSourceの次入力で確認します。</summary>
public sealed class DetachedAdmissionTests
{
    /// <summary>既定で拡張ノードを動かし、制限設定後は副作用開始前に拒否します。</summary>
    /// <returns>ローカル実行の検証です。</returns>
    [Fact]
    public async Task DetachedExecutorUsesConfiguredPurposeRestrictionAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        DetachedPeers peers = new(fixture.Peers);
        NodeExecutor executor = new(fixture.Registry, peers, fixture.Sources, fixture.Safety);
        NodeDefinition node = WorkflowFixture.Definition("n");
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(Request(fixture, node)).ConfigureAwait(true)).Outcome);
        executor.DetachedOptions = new(ExternalCommunication: false);
        NodeResult refused = await executor.ExecuteAsync(Request(fixture, node)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
        Assert.Equal("execution.operationDenied", refused.ReasonCode);
        Assert.Equal(1, calls);
    }

    /// <summary>順番待ちの間に生じた依存競合は、既処理を壊さず後続だけを拒否します。</summary>
    /// <returns>実開始境界の検証です。</returns>
    [Fact]
    public async Task QueuedInputRechecksDependencyImmediatelyBeforeExecutionAsync()
    {
        int calls = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (_, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NodeDefinition node = WorkflowFixture.Definition("n") with { Overlap = EOverlapPolicy.Queue };
        Task<NodeResult> first = fixture.Executor.ExecuteAsync(Request(fixture, node), timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = new TestTaskLifetime(first, timeout).ConfigureAwait(true);
        await started.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Task<NodeResult> queued = fixture.Executor.ExecuteAsync(Request(fixture, node), timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable queuedLifetime = new TestTaskLifetime(queued, timeout).ConfigureAwait(true);
        fixture.Executor.DependencyCheck = static (_, _) => "configuration.conflict";
        release.SetResult();
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await queued.ConfigureAwait(true)).Outcome);
        Assert.Equal(1, calls);
    }

    /// <summary>開始済み一件を完了し、次BeginItemだけ依存理由付きで終了させます。</summary>
    /// <returns>継続入力の実タスクの検証です。</returns>
    [Fact]
    public async Task SourceRechecksDependencyForNextItemWithoutChangingCurrentAsync()
    {
        BoundarySource source = new();
        WorkflowFixture fixture = new(source);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NodeDefinition node = WorkflowFixture.Definition("n", "boundary.source") with { StartOnLoad = true };
        WorkflowDefinition workflow = new("w", "boundary", [node], [], true);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(workflow).Workflow!, timeout.Token).ConfigureAwait(true);
        await source.Begin.Writer.WriteAsync(true, timeout.Token).ConfigureAwait(true);
        await source.Captured.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        fixture.Sources.DependencyCheck = static (_, _) => "configuration.conflict";
        source.Release.TrySetResult();
        await source.CompletedItem.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        await source.Begin.Writer.WriteAsync(true, timeout.Token).ConfigureAwait(true);
        while (fixture.Sources.GetSources().Single().Result is null)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        NodeResult result = fixture.Sources.GetSources().Single().Result!;
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("source.operationDenied", result.ReasonCode);
        Assert.Equal(1, source.Items);
    }

    /// <summary>受信要求に保持された未解消項目を、ローカルの正常版で上書きして開始しません。</summary>
    /// <returns>Source開始境界の確認です。</returns>
    [Fact]
    public async Task SourceRejectsIncomingUnresolvedSnapshotEvenWithCleanLocalProviderAsync()
    {
        BoundarySource source = new("Scheduling");
        WorkflowFixture fixture = new(source);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Sources.OperationalProvider = new OperationalProvider();
        NodeDefinition node = WorkflowFixture.Definition("n", "boundary.source");
        CompiledWorkflow plan = fixture.Compiler.Compile(new("w", "operational", [node], [], true)).Workflow!;
        await fixture.Sources.ApplyAsync(plan).ConfigureAwait(true);
        RemoteExecutionRequest request = Request(fixture, node) with { Revision = plan.Revision, Operational = Snapshot(true) };
        NodeResult result = await fixture.Sources.StartAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("Conflict.OperationalDependency", result.Message);
        Assert.Empty(fixture.Sources.GetSources());
    }

    /// <summary>現一件は保持した版で完了し、未解消版の次入力だけを拒否し、解消後に再開できます。</summary>
    /// <returns>継続Sourceの実入力境界です。</returns>
    [Fact]
    public async Task SourceNextItemChecksCapturedFieldsAndRestartsAfterResolutionAsync()
    {
        BoundarySource source = new("Scheduling");
        WorkflowFixture fixture = new(source);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        OperationalProvider provider = new();
        fixture.Sources.OperationalProvider = provider;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NodeDefinition node = WorkflowFixture.Definition("n", "boundary.source") with { StartOnLoad = true };
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(new("w", "operational", [node], [], true)).Workflow!, timeout.Token).ConfigureAwait(true);
        await source.Begin.Writer.WriteAsync(true, timeout.Token).ConfigureAwait(true);
        await source.Captured.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        provider.Value = Snapshot(true);
        source.Release.TrySetResult();
        await source.CompletedItem.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        await source.Begin.Writer.WriteAsync(true, timeout.Token).ConfigureAwait(true);
        while (fixture.Sources.GetSources().Single().Result is null)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.NotExecuted, fixture.Sources.GetSources().Single().Result!.Outcome);
        Assert.Equal(1, source.Items);
        provider.Value = Snapshot(false);
        await fixture.Sources.ReconcileAsync(timeout.Token).ConfigureAwait(true);
        await source.Begin.Writer.WriteAsync(true, timeout.Token).ConfigureAwait(true);
        while (source.Items != 2)
        {
            // Result公開後もterminal配送は継続するため、実Coreと同じ周期で終了後の再調停を行います。
            await fixture.Sources.ReconcileAsync(timeout.Token).ConfigureAwait(true);
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
    }

    /// <summary>未解消項目だけを変更した共通運用版です。</summary>
    /// <returns>一件に保持する版です。</returns>
    private static ClusterOperationalSnapshot Snapshot(bool unresolved)
    {
        IReadOnlyList<string>? fields = null;
        if (unresolved)
        {
            fields = ["TimeZoneId"];
        }
        return new(new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>()), "version", DateTimeOffset.UnixEpoch, UnresolvedFields: fields);
    }

    /// <summary>次の取得時点だけに変更を渡す設定参照元です。</summary>
    private sealed class OperationalProvider : IClusterOperationalProvider
    {
        internal ClusterOperationalSnapshot Value
        {
            get; set;
        } = Snapshot(false);

        /// <summary>試験で固定した不変レコードを取得します。</summary>
        /// <returns>現在の共通運用版です。</returns>
        public ClusterOperationalSnapshot? Capture() => Value;
    }

    /// <summary>同じ実体の独立した要求です。</summary>
    /// <returns>有限実行要求です。</returns>
    private static RemoteExecutionRequest Request(WorkflowFixture fixture, NodeDefinition node) => new(Guid.NewGuid(), Guid.NewGuid(), "w", "revision", node, new Dictionary<string, NodeValue>(), fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));

    /// <summary>同じ実体の離脱状態だけを差し替えます。</summary>
    private sealed class DetachedPeers(IPeerDirectory inner) : IPeerDirectory
    {
        /// <summary>固定Peerです。</summary>
        public string LocalPeerId => inner.LocalPeerId;

        /// <summary>固定実体です。</summary>
        public Guid LocalInstanceId => inner.LocalInstanceId;

        /// <summary>離脱はCoordinatorへ参加しません。</summary>
        public string? CoordinatorId => null;

        /// <summary>離脱した実体です。</summary>
        /// <returns>ローカルPeer一覧です。</returns>
        public IReadOnlyList<PeerInfo> GetPeers() => inner.GetPeers().Select(static peer => peer with { Status = EPeerStatus.Detached }).ToArray();

        /// <summary>指定Groupを維持します。</summary>
        /// <returns>Group一覧です。</returns>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups() => inner.GetHostGroups();
    }

    /// <summary>取得開始と完了を試験から独立に進めるSourceです。</summary>
    private sealed class BoundarySource(string category = "Control") : ISourceNode
    {
        internal Channel<bool> Begin
        {
            get;
        } = Channel.CreateUnbounded<bool>();

        internal TaskCompletionSource Captured
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CompletedItem
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Items
        {
            get; private set;
        }

        /// <summary>試験用の継続入力です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("boundary.source", "boundary", category, "{}", [], kind: ENodeKind.Source);

        /// <summary>試験の設定は追加制約なしです。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>一件だけを開始し、明示した時点で次入力へ進みます。</summary>
        /// <returns>待受の終了結果です。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            while (!context.IsStopRequested)
            {
                _ = await Begin.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                NodeExecutionContext item = context.BeginItem();
                Items++;
                Captured.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await context.EmitAsync(item, NodeResult.Success(), cancellationToken).ConfigureAwait(false);
                CompletedItem.TrySetResult();
            }
            return NodeResult.Success();
        }
    }
}
