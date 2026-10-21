using System.Collections.Concurrent;
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

/// <summary>元Source セッションだけへ明示取消が届くことを確認します。</summary>
public sealed class SourceCancellationTests
{
    /// <summary>caller トークンを無視する配送でも明示取消し、別ワークフローと再開セッションを維持します。</summary>
    /// <returns>実行・停止・再開の確認です。</returns>
    [Fact]
    public async Task ExplicitCancelReachesOnlyOriginalSourceDispatchAsync()
    {
        TestSource source = new();
        Channel<(string Workflow, CancellationToken Token)> entered = Channel.CreateUnbounded<(string, CancellationToken)>();
        TaskCompletionSource otherFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await entered.Writer.WriteAsync((context.WorkflowId, token), token).ConfigureAwait(false);
            try
            {
                string other = "other";
                if (string.Equals(context.WorkflowId, other, StringComparison.Ordinal))
                {
                    await otherFinish.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                else
                {
                    string valueKey = "value";
                    if (context.Inputs[valueKey].Json.GetInt32() == 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }
                }
                return NodeResult.Success();
            }
            catch (OperationCanceledException)
            {
                return NodeResult.Failure("test.cancelled", "停止確認", EOutcome.Cancelled);
            }
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        fixture.Dispatcher.Handler = (request, _) => fixture.Executor.ExecuteAsync(request, CancellationToken.None);
        List<RemoteExecutionRequest> cancellations = [];
        fixture.Engine.NodeCancellationHandler = async (request, token) =>
        {
            cancellations.Add(request);
            return await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt, token).ConfigureAwait(false);
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        CompiledWorkflow plan = fixture.Compiler.Compile(Definition("source-workflow")).Workflow!;
        await fixture.Sources.ApplyAsync(plan, timeout.Token);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("other")).Workflow!, timeout.Token);
        await source.EmitAsync("source-workflow", 1, timeout.Token);
        (string _, CancellationToken original) = await entered.Reader.ReadAsync(timeout.Token);
        await source.EmitAsync("other", 1, timeout.Token);
        (string _, CancellationToken independent) = await entered.Reader.ReadAsync(timeout.Token);
        NodeResult stopped = await fixture.Sources.StopAsync("source-workflow", "source", true, cancellationToken: timeout.Token);
        Assert.Equal(EOutcome.Cancelled, stopped.Outcome);
        Assert.True(original.IsCancellationRequested);
        Assert.False(independent.IsCancellationRequested);
        Assert.Equal("source-workflow", Assert.Single(cancellations).WorkflowId);
        otherFinish.TrySetResult();
        await fixture.Sources.StartAsync(Request(fixture, plan), timeout.Token);
        await source.EmitAsync("source-workflow", 2, timeout.Token);
        (string _, CancellationToken restarted) = await entered.Reader.ReadAsync(timeout.Token);
        Assert.False(restarted.IsCancellationRequested);
        Assert.Single(cancellations);
    }

    /// <summary>停止保持中の入力は未実行にし、解除後に古い入力を再開しません。</summary>
    /// <returns>保持領域と新しいセッションの確認です。</returns>
    [Fact]
    public async Task CancelDropsHeldInputsButRestartAcceptsNewInputAsync()
    {
        TestSource source = new();
        Channel<int> observed = Channel.CreateUnbounded<int>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context.Inputs["value"].Json.GetInt32(), token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        CompiledWorkflow plan = fixture.Compiler.Compile(Definition("held")).Workflow!;
        await fixture.Sources.ApplyAsync(plan, timeout.Token);
        fixture.Safety.SetStopped("held", "source", true);
        await source.EmitAsync("held", 1, timeout.Token);
        Assert.Equal(1, fixture.Sources.Statistics.Held);
        await fixture.Sources.StopAsync("held", "source", true, cancellationToken: timeout.Token);
        Assert.Equal(0, fixture.Sources.Statistics.Held);
        fixture.Safety.SetStopped("held", "source", false);
        await fixture.Sources.StartAsync(Request(fixture, plan), timeout.Token);
        await source.EmitAsync("held", 2, timeout.Token);
        Assert.Equal(2, await observed.Reader.ReadAsync(timeout.Token));
        Assert.False(observed.Reader.TryRead(out _));
    }

    /// <summary>取消が実配送より先に到着しても、一件を成功へ読み替えません。</summary>
    /// <returns>副作用未開始と取消集約の確認です。</returns>
    [Fact]
    public async Task CancellationIntentBeforeDeliveryCancelsWholeInputAsync()
    {
        TestSource source = new();
        int calls = 0;
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        TaskCompletionSource delivering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource delivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatcher.Handler = async (request, _) =>
        {
            delivering.TrySetResult();
            await delivery.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return await fixture.Executor.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
        };
        fixture.Engine.NodeCancellationHandler = async (request, token) =>
        {
            bool accepted = await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt, token).ConfigureAwait(false);
            delivery.TrySetResult();
            return accepted;
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("before-delivery")).Workflow!, timeout.Token);
        await source.EmitAsync("before-delivery", 1, timeout.Token);
        await delivering.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, (await fixture.Sources.StopAsync("before-delivery", "source", true, cancellationToken: timeout.Token)).Outcome);
        Assert.Equal(0, calls);
        Assert.DoesNotContain(fixture.Engine.Recent(), report => report.Outcome == EOutcome.Succeeded);
    }

    /// <summary>取消配送を確認できなければ、続行中の実処理を停止済みとしません。</summary>
    /// <returns>Unknownと継続中トークンの確認です。</returns>
    [Fact]
    public async Task RejectedCancellationDoesNotClaimActualStopAsync()
    {
        TestSource source = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actual = default;
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (_, token) =>
        {
            actual = token;
            entered.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = (request, _) => fixture.Executor.ExecuteAsync(request, CancellationToken.None);
        fixture.Engine.NodeCancellationHandler = (_, _) => Task.FromResult(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("unconfirmed")).Workflow!, timeout.Token);
        await source.EmitAsync("unconfirmed", 1, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, (await fixture.Sources.StopAsync("unconfirmed", "source", true, cancellationToken: timeout.Token)).Outcome);
        Assert.False(actual.IsCancellationRequested);
        finish.TrySetResult();
    }

    /// <summary>通常入力だけを有限処理へ渡します。</summary>
    /// <returns>独立した一つのワークフローです。</returns>
    private static WorkflowDefinition Definition(string id) => new(id, id,
        [WorkflowFixture.Definition("source", "test.cancelSource") with { StartOnLoad = true, Safety = new(false, false, EStoppedInputPolicy.All) }, WorkflowFixture.Definition("sink")],
        [new("source", "value", "sink", "value")], true);

    /// <summary>新しいセッションは新しい開始要求で明示します。</summary>
    /// <returns>現在の実体に固定した開始です。</returns>
    private static RemoteExecutionRequest Request(WorkflowFixture fixture, CompiledWorkflow plan) => new(Guid.NewGuid(), Guid.NewGuid(), plan.Definition.Id, plan.Revision, plan.Definition.Nodes[0], new Dictionary<string, NodeValue>(), fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));

    /// <summary>ワークフローごとに独立した継続待受です。</summary>
    private sealed class TestSource : ISourceNode
    {
        private readonly ConcurrentDictionary<string, NodeSourceContext> contexts = new(StringComparer.Ordinal);

        /// <summary>試験用Sourceの契約です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.cancelSource", "入力", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>設定の追加制約はありません。</summary>
        /// <returns>問題なし。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>呼出元が停止するまで待ちます。</summary>
        /// <returns>正常停止または取消。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            contexts[context.Initial.WorkflowId] = context;
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return NodeResult.Success();
            }
            return NodeResult.Success();
        }

        /// <summary>現在セッションから一件を受付します。</summary>
        /// <returns>受付の完了です。</returns>
        internal ValueTask EmitAsync(string workflow, int value, CancellationToken cancellationToken = default)
        {
            NodeSourceContext context = contexts[workflow];
            return context.EmitAsync(context.BeginItem(), NodeResult.Success(NodeValue.FromObject(value)), cancellationToken);
        }
    }
}
