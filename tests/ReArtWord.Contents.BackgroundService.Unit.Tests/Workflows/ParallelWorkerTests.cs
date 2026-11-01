using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>並列反復は最初の非同期待ちだけに依存せず、同期的な処理も指定枠で並走します。</summary>
public sealed class ParallelWorkerTests
{
    /// <summary>同期処理の先行要素が終了する前に、二つ目の要素も実際に開始します。</summary>
    /// <returns>非同期I/Oを持たないボディの並走と入力順の結果です。</returns>
    [Fact]
    public async Task SynchronousBodiesCanEnterBothWorkersBeforeEitherFinishes()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<int> received = [];
        WorkflowFixture fixture = new(new ForEachNode(true), new ForEachEndNode(), new SynchronousNode(context =>
        {
            received.Add(context.Inputs["value"].Json.GetInt32());
            if (received.Count == 2)
            {
                both.TrySetResult();
            }
            release.Wait();
            return NodeResult.Success(context.Inputs["value"]);
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        Task<ExecutionReport> execution = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition("[1,2]", parallel: true)));
        try
        {
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            release.Set();
            await execution.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await execution.ConfigureAwait(true)).Outcome);
        Assert.Equal([1, 2], received.Order());
    }

    /// <summary>ValueTaskを同期完了する通常の計算ノード相当です。</summary>
    private sealed class SynchronousNode(Func<NodeExecutionContext, NodeResult> execute) : IActionNode
    {
        /// <summary>非同期完了出力を持たない通常の値処理です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.action", "sync", "test", "{}", [new("value", Required: true)], [new("value")]);

        /// <summary>試験用の設定制約はありません。</summary>
        /// <returns>追加の問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>同期実行の最中にも他のworkerが進行するかを確認します。</summary>
        /// <returns>その入力だけの結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(execute(context));
    }
}
