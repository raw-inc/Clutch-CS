using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>元Sourceからの明示取消が反復の実配送と未開始要素へ届くことを確認します。</summary>
public sealed class IterationSourceCancellationTests
{
    /// <summary>通常のcaller切断と別の取消経路を使い、同じ元入力の要求だけ停止します。</summary>
    /// <returns>取消後に次要素を開始しない結果です。</returns>
    [Fact]
    public async Task ExplicitSourceCancelReachesIterationBodyAsync()
    {
        ArraySource source = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        WorkflowFixture fixture = new(source, new ForEachNode(), new WorkflowFixture.ActionNode(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                return NodeResult.Success();
            }
            catch (OperationCanceledException)
            {
                return NodeResult.Failure("test.cancelled", "Stopped", EOutcome.Cancelled);
            }
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = (request, _) => fixture.Executor.ExecuteAsync(request, CancellationToken.None);
        List<RemoteExecutionRequest> requested = [];
        fixture.Engine.NodeCancellationHandler = async (request, token) =>
        {
            requested.Add(request);
            return await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt, token).ConfigureAwait(false);
        };
        WorkflowDefinition original = IterationTests.Definition(end: false);
        WorkflowDefinition definition = original with
        {
            Enabled = true,
            Nodes = [WorkflowFixture.Definition("source", "test.iterationSource") with { StartOnLoad = true }, original.Nodes[0] with { Inputs = null }, original.Nodes[1]],
            Wires = [new("source", "value", "each", "value"), .. original.Wires],
        };
        CompiledWorkflow plan = IterationTests.Compile(fixture, definition);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        Guid cause = await source.EmitAsync(timeout.Token).ConfigureAwait(true);
        await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeResult stopped = await fixture.Sources.StopAsync(definition.Id, "source", true, cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, stopped.Outcome);
        Assert.Equal(1, calls);
        RemoteExecutionRequest cancellation = Assert.Single(requested);
        Assert.Equal("body", cancellation.Node.Id);
        Assert.Equal(cause, cancellation.CausationId);
        ExecutionReport? report = fixture.Engine.Find(cause);
        Assert.NotNull(report);
        Assert.Equal(EOutcome.Cancelled, report.Outcome);
        Assert.Equal(1, IterationTests.Loop(report).Iteration!.Started);
    }

    /// <summary>配列を一件として発生させる継続入力試験用の構成です。</summary>
    private sealed class ArraySource : ISourceNode
    {
        private static readonly int[] values = [1, 2, 3];
        private readonly TaskCompletionSource<NodeSourceContext> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>起動入力なしのSourceです。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.iterationSource", "Array source", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>試験用の設定を受け付けます。</summary>
        /// <returns>追加の制約はありません。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>停止操作までSourceの寿命を保ちます。</summary>
        /// <returns>通常終了と明示取消を分離します。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            ready.TrySetResult(context);
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

        /// <summary>一件の元識別子を保持して配列を発生させます。</summary>
        /// <returns>発生元の原因識別子です。</returns>
        internal async Task<Guid> EmitAsync(CancellationToken cancellationToken = default)
        {
            NodeSourceContext context = await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            NodeExecutionContext item = context.BeginItem();
            await context.EmitAsync(item, NodeResult.Success(NodeValue.FromObject(values)), cancellationToken).ConfigureAwait(false);
            return item.CausationId;
        }
    }
}
