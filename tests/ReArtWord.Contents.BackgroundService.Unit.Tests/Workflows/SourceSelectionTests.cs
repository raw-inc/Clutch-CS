using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>Sourceの選択・取得済み入力・予定終了を同じ共通版で扱うことを確認します。</summary>
public sealed class SourceSelectionTests
{
    /// <summary>取得済み一件と終了通知は旧版で続き、新規対象外入力は開始せず再選択できます。</summary>
    /// <returns>世代と正常終了・再開の検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedHostStopsBeforeNextItemAndKeepsCapturedItemAndTerminalPolicy(bool unavailable)
    {
        SelectionSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(false);
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        ClusterOperationalSnapshot? current = Snapshot("old", true);
        OperationalProvider operational = new(() => current);
        TraceProvider trace = new() { Enabled = true };
        fixture.Sources.OperationalProvider = operational;
        fixture.Engine.OperationalProvider = operational;
        fixture.Sources.TracePolicyProvider = trace;
        fixture.Engine.TracePolicyProvider = trace;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition()).Workflow!, timeout.Token);
        NodeExecutionContext captured = await source.Captured.Reader.ReadAsync(timeout.Token);
        Assert.Equal("old", captured.Operational!.Revision);
        Assert.True(captured.TracePolicy!.Enabled);
        current = Snapshot("excluded", false);
        if (unavailable)
        {
            current = null;
        }
        trace.Enabled = false;
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(7), timeout.Token);
        NodeExecutionContext first = await observed.Reader.ReadAsync(timeout.Token);
        NodeExecutionContext second = await observed.Reader.ReadAsync(timeout.Token);
        foreach (NodeExecutionContext item in new[] { first, second })
        {
            Assert.Equal("old", item.Operational!.Revision);
            Assert.True(item.TracePolicy!.Enabled);
        }
        Assert.NotEqual(first.CausationId, second.CausationId);
        await WaitAsync(() => fixture.Sources.GetSources().Single().Status.State == ESourceState.Completed, timeout.Token);
        Assert.Equal(1, source.Starts);
        Assert.False(source.Captured.Reader.TryRead(out _));
        await fixture.Sources.ReconcileAsync(timeout.Token);
        Assert.Equal(1, source.Starts);
        current = Snapshot("restored", true);
        await fixture.Sources.ReconcileAsync(timeout.Token);
        NodeExecutionContext next = await source.Captured.Reader.ReadAsync(timeout.Token);
        Assert.Equal(2, source.Starts);
        Assert.Equal("restored", next.Operational!.Revision);
        Assert.False(next.TracePolicy!.Enabled);
    }

    /// <summary>Reconcileと予約の間に所属が変われば、予約内の現在版で開始を拒否します。</summary>
    /// <returns>未開始の検証完了。</returns>
    [Fact]
    public async Task StartRechecksMembershipInsteadOfUsingReconcileSnapshotOrDirectoryCache()
    {
        SelectionSource source = new();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((context, token) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        int captures = 0;
        fixture.Sources.OperationalProvider = new OperationalProvider(() => Snapshot("v" + captures, Interlocked.Increment(ref captures) == 1));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition()).Workflow!);
        Assert.Equal(0, source.Starts);
        Assert.Empty(fixture.Sources.GetSources());
        Assert.Equal(2, captures);
    }

    /// <summary>追加した未発見Hostで既存メンバーの継続待受を停止しません。</summary>
    /// <returns>既存待受の継続と有限Allの厳密性を確認します。</returns>
    [Fact]
    public async Task UnknownAddedHostDoesNotStopExistingSourceMemberAsync()
    {
        SelectionSource source = new();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        ClusterOperationalSnapshot current = Snapshot("initial", true);
        fixture.Sources.OperationalProvider = new OperationalProvider(() => current);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition()).Workflow!, timeout.Token);
        _ = await source.Captured.Reader.ReadAsync(timeout.Token);
        current = current with { Revision = "added", Settings = current.Settings with { HostGroups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["sources"] = ["host-a", "not-discovered"] } } };
        await fixture.Sources.ReconcileAsync(timeout.Token);
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(1), timeout.Token);
        NodeExecutionContext next = await source.Captured.Reader.ReadAsync(timeout.Token);
        Assert.Equal("added", next.Operational!.Revision);
        Assert.Equal(1, source.Starts);
        TargetResolution finite = new TargetResolver(fixture.Peers).Resolve(Definition().Nodes[0], source.Descriptor, hostGroups: current.Settings.HostGroups);
        Assert.Equal("target.hostMissing", finite.Error!.ReasonCode);
    }

    /// <summary>Sourceだけを共通Host Groupへ割り当てます。</summary>
    /// <returns>通常入力と終了通知を別々に観測できる定義です。</returns>
    private static WorkflowDefinition Definition() => new("w", "source group",
        [WorkflowFixture.Definition("source", "test.selection") with { StartOnLoad = true, Target = new TargetDefinition(EExecutionScope.All, Groups: ["sources"]) },
            WorkflowFixture.Definition("sink"), WorkflowFixture.Definition("terminal")],
        [new WireDefinition("source", "value", "sink", "value"), new WireDefinition("source", "result", "terminal", "value", When: "completed")], true);

    /// <summary>ディレクトリキャッシュと独立した所属を持つ共通版です。</summary>
    /// <returns>一件に固定する版です。</returns>
    private static ClusterOperationalSnapshot Snapshot(string revision, bool included)
    {
        IReadOnlyList<string> hosts = [];
        if (included)
        {
            hosts = ["host-a"];
        }
        return new(new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["sources"] = hosts }), revision, DateTimeOffset.UtcNow);
    }

    /// <summary>待受の実終了を有限時間で確認します。</summary>
    /// <returns>条件成立です。</returns>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>取得する時点の共通版を返します。</summary>
    private sealed class OperationalProvider(Func<ClusterOperationalSnapshot?> capture) : IClusterOperationalProvider
    {
        /// <summary>現在版です。</summary>
        /// <returns>指定した版です。</returns>
        public ClusterOperationalSnapshot? Capture() => capture();
    }

    /// <summary>一時追跡の切替を再現します。</summary>
    private sealed class TraceProvider : ITracePolicyProvider
    {
        /// <summary>次の一件に適用する方針です。</summary>
        public bool Enabled
        {
            get; set;
        }

        /// <summary>新しい一件へ値を固定します。</summary>
        /// <returns>その時点の追跡方針です。</returns>
        public TracePolicy GetPolicy(string workflowId, TracePolicy saved) => new(Enabled);
    }

    /// <summary>取得開始と内容完了を別々に進めるSourceです。</summary>
    private sealed class SelectionSource : ISourceNode
    {
        private int starts;

        /// <summary>実開始回数です。</summary>
        public int Starts => Volatile.Read(ref starts);

        /// <summary>取得した文脈です。</summary>
        public Channel<NodeExecutionContext> Captured
        {
            get;
        } = Channel.CreateUnbounded<NodeExecutionContext>();

        /// <summary>開始済み入力を完了させる内容です。</summary>
        public Channel<NodeValue> Items
        {
            get;
        } = Channel.CreateUnbounded<NodeValue>();

        /// <summary>試験用の継続入力です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.selection", "選択", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>追加の設定はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>一件の文脈を保持してから入力完了を待ちます。</summary>
        /// <returns>正常終了または取消です。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref starts);
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            while (!context.IsStopRequested)
            {
                NodeExecutionContext item = context.BeginItem();
                await Captured.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                NodeValue value = await Items.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                await context.EmitAsync(item, NodeResult.Success(value), cancellationToken).ConfigureAwait(false);
            }
            return NodeResult.Success();
        }
    }
}
