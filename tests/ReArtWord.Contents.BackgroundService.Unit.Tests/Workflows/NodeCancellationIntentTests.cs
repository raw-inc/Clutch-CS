using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>配送より先の明示取消と、元期限後の進行中取消を検証します。</summary>
public sealed class NodeCancellationIntentTests
{
    /// <summary>先に届いた取消でノードを開始せず、古い未開始要求も拒否します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CancellationBeforeAdmissionPreventsSideEffectAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        RemoteExecutionRequest request = Request(fixture, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt));
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Executor.ExecuteAsync(request)).Outcome);
        RemoteExecutionRequest expired = request with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        Assert.False(await fixture.Executor.RequestCancellationAsync(expired.Id, expired.ExpiresAt));
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Executor.ExecuteAsync(expired)).Outcome);
        Assert.Equal(0, calls);
    }

    /// <summary>開始済み処理は元の受付期限を過ぎても取消でき、元結果で確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RunningWorkCanBeCancelledAfterOriginalDeadlineAsync()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                return NodeResult.Success();
            }
            catch (OperationCanceledException)
            {
                return NodeResult.Failure("test.cancelled", "実停止確認", EOutcome.Cancelled);
            }
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        TestClock clock = new();
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, timeProvider: clock);
        RemoteExecutionRequest request = Request(fixture, clock.GetUtcNow().AddMinutes(1));
        Task<NodeResult> execution = executor.ExecuteAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        clock.Now = clock.Now.AddHours(1);
        Assert.True(await executor.RequestCancellationAsync(request.Id, request.ExpiresAt));
        Assert.Equal(EOutcome.Cancelled, (await execution.ConfigureAwait(true)).Outcome);
    }

    /// <summary>未開始intentは有限件数で、期限後の新規受付で解放します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task PendingIntentsAreBoundedAndExpireAsync()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        TestClock clock = new();
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, timeProvider: clock);
        for (int index = 0; index < 1024; index++)
        {
            Assert.True(await executor.RequestCancellationAsync(Guid.NewGuid(), clock.Now.AddMinutes(1)));
        }
        Assert.False(await executor.RequestCancellationAsync(Guid.NewGuid(), clock.Now.AddMinutes(1)));
        clock.Now = clock.Now.AddHours(1);
        Assert.True(await executor.RequestCancellationAsync(Guid.NewGuid(), clock.Now.AddMinutes(1)));
    }

    /// <summary>ローカル実体への一回要求です。</summary>
    /// <returns>明示した期限を持つ要求です。</returns>
    private static RemoteExecutionRequest Request(WorkflowFixture fixture, DateTimeOffset expires) => new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", WorkflowFixture.Definition("action"), new Dictionary<string, NodeValue>(), fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, expires);

    /// <summary>受付期限だけを確実に進めます。</summary>
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now
        {
            get; set;
        } = DateTimeOffset.UtcNow;

        /// <summary>試験の現在時刻です。</summary>
        /// <returns>制御されたUTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
