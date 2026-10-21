using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>待受の実タスクで取得開始・設定反映・正常停止の境界を検証します。</summary>
public sealed class SourceLifecycleTests
{
    /// <summary>自動開始OFFでも明示実行でき、ON/OFFの反映だけでは開始済みの待受を作り直しません。</summary>
    /// <returns>開始条件と、実行中セッションの寿命が独立している確認です。</returns>
    [Fact]
    public async Task AutomaticStartupCanBeDisabledWithoutDisablingExplicitExecution()
    {
        ControlledSource source = new();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition definition = Definition();
        definition = definition with { Nodes = [definition.Nodes[0] with { StartOnLoad = false }, definition.Nodes[1]] };
        CompiledWorkflow plan = fixture.Compiler.Compile(definition).Workflow!;
        await fixture.Sources.ApplyAsync(plan, deadline.Token).ConfigureAwait(true);
        Assert.Empty(fixture.Sources.GetSources());
        ExecutionReport started = await fixture.Engine.ExecuteAsync(plan, "source", cancellationToken: deadline.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        _ = await source.CaptureAsync(deadline.Token).ConfigureAwait(true);
        Guid session = Assert.Single(fixture.Sources.GetSources()).Id;
        foreach (bool automatic in new[] { true, false })
        {
            definition = definition with { Nodes = [definition.Nodes[0] with { StartOnLoad = automatic }, definition.Nodes[1]] };
            plan = fixture.Compiler.Compile(definition).Workflow!;
            await fixture.Sources.ApplyAsync(plan, deadline.Token).ConfigureAwait(true);
            Assert.Equal(session, Assert.Single(fixture.Sources.GetSources()).Id);
            Assert.Equal(ESourceState.Listening, Assert.Single(fixture.Sources.GetSources()).Status.State);
            Assert.Equal(1, source.Starts);
        }
    }

    /// <summary>任意期限は正常終了し、受付済みの入力は取消しません。</summary>
    /// <returns>有限待受、後続への完了通知と再開抑止の確認。</returns>
    [Fact]
    public async Task ListeningDurationDrainsAcceptedWorkAndCompletesOnceAsync()
    {
        ControlledSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, cancellation) =>
        {
            await observed.Writer.WriteAsync(context, cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition definition = Definition();
        definition = definition with { Nodes = [definition.Nodes[0] with { ListenDurationMs = 100 }, definition.Nodes[1]],
            Wires = [.. definition.Wires, new("source", "result", "sink", "trigger", When: "completed")] };
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        await fixture.Sources.ApplyAsync(compiled.Workflow!, timeout.Token).ConfigureAwait(true);
        _ = await source.CaptureAsync(timeout.Token).ConfigureAwait(true);
        await Task.Delay(180, timeout.Token).ConfigureAwait(true);
        Assert.Equal(ESourceState.Listening, Assert.Single(fixture.Sources.GetSources()).Status.State);
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(7), timeout.Token).ConfigureAwait(true);
        _ = await observed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        while (Assert.Single(fixture.Sources.GetSources()).Status.State == ESourceState.Listening)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(ESourceState.Completed, Assert.Single(fixture.Sources.GetSources()).Status.State);
        await fixture.Sources.ReconcileAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, source.Starts);
    }

    /// <summary>同じノードを再開した後、元の待受に対する停止要求では新しい待受を停止しません。</summary>
    /// <returns>古い停止要求の拒否と、新しい待受の継続確認。</returns>
    [Fact]
    public async Task StaleStopRequestDoesNotCancelRestartedSourceAsync()
    {
        ControlledSource source = new();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        CompiledWorkflow plan = fixture.Compiler.Compile(Definition()).Workflow!;
        await fixture.Sources.ApplyAsync(plan, timeout.Token);
        Guid original = Assert.Single(fixture.Sources.GetSources()).Id;
        await fixture.Sources.StopAsync("w", "source", true, original, cancellationToken: timeout.Token);
        NodeDefinition node = plan.FindNode("source")!;
        Guid restarted = Guid.NewGuid();
        NodeResult started = await fixture.Sources.StartAsync(new RemoteExecutionRequest(restarted, Guid.NewGuid(), "w", plan.Revision, node,
            new Dictionary<string, NodeValue>(), fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1)), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        NodeResult stale = await fixture.Sources.StopAsync("w", "source", true, original, cancellationToken: timeout.Token);
        Assert.Equal(EOutcome.NotExecuted, stale.Outcome);
        Assert.Equal("source.instanceChanged", stale.ReasonCode);
        Assert.Contains(fixture.Sources.GetSources(), item => item.Id == restarted && item.Status.State == ESourceState.Listening);
    }

    /// <summary>受信開始後の編集を下流へ遡及させず、次の一件から反映します。</summary>
    /// <returns>実タスクの非同期検証です。</returns>
    [Fact]
    public async Task InFlightItemKeepsItsGraphAndNextItemUsesNewGraph()
    {
        ControlledSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, cancellation) =>
        {
            await observed.Writer.WriteAsync(context, cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition definition = Definition();
        CompiledWorkflow first = fixture.Compiler.Compile(definition).Workflow!;
        await fixture.Sources.ApplyAsync(first, timeout.Token);
        _ = await source.CaptureAsync(timeout.Token);
        WorkflowDefinition edited = definition with { Nodes = [definition.Nodes[0], WorkflowFixture.Definition("sink", settings: """{"version":2}""")] };
        CompiledWorkflow next = fixture.Compiler.Compile(edited).Workflow!;
        await fixture.Sources.ApplyAsync(next, timeout.Token);
        Assert.Equal(next.Revision, Assert.Single(fixture.Sources.GetSources()).Revision);
        Assert.Equal(ESourceState.Listening, Assert.Single(fixture.Sources.GetSources()).Status.State);
        Assert.Equal(1, source.Starts);

        await source.Items.Writer.WriteAsync(NodeValue.FromObject(1), timeout.Token);
        NodeExecutionContext oldItem = await observed.Reader.ReadAsync(timeout.Token);
        _ = await source.CaptureAsync(timeout.Token);
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(2), timeout.Token);
        NodeExecutionContext newItem = await observed.Reader.ReadAsync(timeout.Token);

        Assert.Equal(first.Revision, oldItem.Revision);
        Assert.Equal(1, oldItem.Settings.GetProperty("version").GetInt32());
        Assert.Equal(next.Revision, newItem.Revision);
        Assert.Equal(2, newItem.Settings.GetProperty("version").GetInt32());
        Assert.NotEqual(oldItem.CausationId, newItem.CausationId);
    }

    /// <summary>通常停止は受付済みの一件を完了させ、人の停止を自動再開しません。</summary>
    /// <returns>実タスクの非同期検証です。</returns>
    [Fact]
    public async Task NormalStopDrainsCurrentItemAndDoesNotRestartOnReconcile()
    {
        ControlledSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, cancellation) =>
        {
            await observed.Writer.WriteAsync(context, cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition()).Workflow!, timeout.Token);
        _ = await source.CaptureAsync(timeout.Token);

        Task<NodeResult> stopping = fixture.Sources.StopAsync("w", "source", cancellationToken: timeout.Token);
        Assert.False(stopping.IsCompleted);
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(1), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await stopping.ConfigureAwait(true)).Outcome);
        _ = await observed.Reader.ReadAsync(timeout.Token);
        await fixture.Sources.ReconcileAsync(timeout.Token);

        Assert.Equal(1, source.Starts);
        Assert.Equal(ESourceState.Completed, Assert.Single(fixture.Sources.GetSources()).Status.State);
    }

    /// <summary>Sourceの受付完了は、下流の副作用の完了待ちと別です。</summary>
    /// <returns>応答構成用の非同期検証です。</returns>
    [Fact]
    public async Task SourceEmissionDoesNotWaitForDownstreamCompletion()
    {
        ControlledSource source = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (_, cancellation) =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(cancellation).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition()).Workflow!, timeout.Token);
        _ = await source.CaptureAsync(timeout.Token);
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(1), timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);

        NodeExecutionContext second = await source.CaptureAsync(timeout.Token);

        Assert.False(finish.Task.IsCompleted);
        Assert.Equal("source", second.NodeId);
        finish.TrySetResult();
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(2), timeout.Token);
    }

    /// <summary>同じ登録で下流設定だけを変更できる定義です。</summary>
    /// <returns>有効なSourceと必須入力の接続です。</returns>
    private static WorkflowDefinition Definition() => new("w", "source lifecycle",
        [WorkflowFixture.Definition("source", "test.source") with { StartOnLoad = true }, WorkflowFixture.Definition("sink", settings: """{"version":1}""")],
        [new WireDefinition("source", "value", "sink", "value")], true);

    /// <summary>取得開始と完了を別々に進められる入力です。</summary>
    private sealed class ControlledSource : ISourceNode
    {
        private readonly Channel<TaskCompletionSource<NodeExecutionContext>> admissions = Channel.CreateUnbounded<TaskCompletionSource<NodeExecutionContext>>();
        private int starts;

        /// <summary>一件を完了させるデータです。</summary>
        internal Channel<NodeValue> Items
        {
            get;
        } = Channel.CreateUnbounded<NodeValue>();

        /// <summary>実際に呼ばれた待受の回数です。</summary>
        internal int Starts => starts;

        /// <summary>有限ノードと違う寿命を公開します。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.source", "入力", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>追加設定はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>明示した時点で一件の設定版を取得します。</summary>
        /// <returns>実Sourceが取得した文脈です。</returns>
        internal async Task<NodeExecutionContext> CaptureAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<NodeExecutionContext> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await admissions.Writer.WriteAsync(captured, cancellationToken).ConfigureAwait(false);
            return await captured.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>受付停止は待機だけを起こし、受付済み入力の完了を待ちます。</summary>
        /// <returns>予定正常終了または取消です。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref starts);
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
            await context.ReportStatusAsync(new NodeSourceStatus(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            try
            {
                while (!context.IsStopRequested)
                {
                    TaskCompletionSource<NodeExecutionContext> captured = await admissions.Reader.ReadAsync(waiting.Token).ConfigureAwait(false);
                    NodeExecutionContext item = context.BeginItem();
                    captured.TrySetResult(item);
                    NodeValue value = await Items.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    await context.EmitAsync(item, NodeResult.Success(value), cancellationToken).ConfigureAwait(false);
                }
                return NodeResult.Success();
            }
            catch (OperationCanceledException) when (context.IsStopRequested && !cancellationToken.IsCancellationRequested)
            {
                return NodeResult.Success();
            }
        }
    }
}
