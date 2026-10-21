using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>ノード本体の外で保存待ちになっても、元WFの取消を待機へ伝えます。</summary>
public sealed class WorkflowStorageCancellationTests
{
    /// <summary>開始前保存と完了値保存の両方で停止でき、実施済みの副作用を再実行しません。</summary>
    /// <returns>取消受付だけで実行中が残らないことです。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExplicitCancellationInterruptsRetentionWait(int heldWrite)
    {
        int effects = 0;
        WorkflowFixture.ActionNode action = new((_, _) =>
        {
            Interlocked.Increment(ref effects);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(42)));
        });
        WorkflowFixture fixture = new(new IfNode(), action);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WaitingStore store = new(heldWrite);
        fixture.Engine.RetainedValues = store;
        NodeDefinition gate = new("gate", "control.ifStart", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["condition"] = NodeValue.FromObject(true) });
        WorkflowDefinition definition = new("waiting", "waiting", [gate, WorkflowFixture.Definition("action")], [new("gate", "start", "action", "trigger")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        Guid id = Guid.NewGuid();
        Task<ExecutionReport> pending = fixture.Engine.ExecuteAsync(compiled.Workflow!, "gate", executionId: id);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        bool interrupted;
        bool finished;
        try
        {
            Assert.True(await fixture.Engine.RequestCancellationAsync(id).ConfigureAwait(true));
            _ = await Task.WhenAny(store.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(true);
            interrupted = store.Cancelled.Task.IsCompleted;
            _ = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(true);
            finished = pending.IsCompleted;
        }
        finally
        {
            store.Release.TrySetResult();
        }
        ExecutionReport report = await pending.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        Assert.True(interrupted, "The workflow cancellation did not reach the retention wait.");
        Assert.True(finished, "The workflow stayed active until storage was released separately.");
        Assert.Empty(fixture.Engine.Active());
        Assert.Equal(heldWrite - 1, effects);
        if (heldWrite == 2)
        {
            NodeResult result = Assert.Single(Assert.Single(report.Nodes, node => node.NodeId is "action").Targets).Result;
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal("value.retentionUnconfirmed", result.ReasonCode);
        }
    }

    /// <summary>同じノードの並行開始を保存区間で揃え、比較失敗の連鎖を発生させません。</summary>
    /// <returns>すべての副作用を一回ずつ実施し、保存要求が入力数に比例することです。</returns>
    [Fact]
    public async Task ConcurrentSignalsDoNotMultiplyRetentionWrites()
    {
        int effects = 0;
        WorkflowFixture.ActionNode action = new((_, _) =>
        {
            Interlocked.Increment(ref effects);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(42)));
        });
        WorkflowFixture fixture = new(new IfNode(), action);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        DelayedStore store = new();
        fixture.Engine.RetainedValues = store;
        NodeDefinition gate = new("gate", "control.ifStart", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["condition"] = NodeValue.FromObject(true) });
        WorkflowDefinition definition = new("burst", "burst", [gate, WorkflowFixture.Definition("action")], [new("gate", "start", "action", "trigger")]);
        CompiledWorkflow compiled = fixture.Compiler.Compile(definition).Workflow!;
        ExecutionReport[] reports = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => fixture.Engine.ExecuteAsync(compiled, "gate")));
        Assert.Equal(24, effects);
        Assert.All(reports, report => Assert.Equal(EOutcome.Succeeded, report.Outcome));
        Assert.InRange(store.Writes, 48, 72);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>複製待ちの間にも次の入力が到着する保存先です。</summary>
    private sealed class DelayedStore : INodeStateStore
    {
        private readonly TestStateStore inner = new();
        private int writes;

        internal int Writes => Volatile.Read(ref writes);

        /// <summary>確定した現在版を取得します。</summary>
        /// <returns>保存済みの値です。</returns>
        public ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => inner.ReadAsync(scope, key, cancellationToken);

        /// <summary>非同期待機後に原子的な比較更新を行います。</summary>
        /// <returns>比較更新の確定結果です。</returns>
        public async ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            return await inner.CompareExchangeAsync(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実行済みの値を変えず、指定した保存境界だけを停止します。</summary>
    private sealed class WaitingStore(int heldWrite) : INodeStateStore
    {
        private readonly TestStateStore inner = new();
        private int writes;

        /// <summary>保存待ちへ到着した通知です。</summary>
        internal TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>元の実行取消が待機へ届いた通知です。</summary>
        internal TaskCompletionSource Cancelled
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>不具合時も試験タスクを残さない後始末です。</summary>
        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>確定済みの値だけを返します。</summary>
        /// <returns>保存済み状態です。</returns>
        public ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => inner.ReadAsync(scope, key, cancellationToken);

        /// <summary>任意のストレージ待ちが取消を受け取れることを確認します。</summary>
        /// <returns>保存結果です。</returns>
        public async ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writes) == heldWrite)
            {
                Entered.TrySetResult();
                try
                {
                    await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Cancelled.TrySetResult();
                    }
                }
            }
            return await inner.CompareExchangeAsync(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        }
    }
}
