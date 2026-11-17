using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>配送より先の明示取消と、元期限後の進行中取消を検証します。</summary>
public sealed class NodeCancellationIntentTests
{
    /// <summary>別PCから届いた実行要求を取り消す場合も、取消要求内の日時が送信側の時刻系に揃うことを確認します。</summary>
    /// <returns>元の実行要求を変更せず、残り時間を取消要求へ引き継ぐことを検証するタスクです。</returns>
    [Fact]
    public async Task ForwardedCancellationUsesTheRelaysClock()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ManagementTransport.AdmissionTestClock clock = new();
        DateTimeOffset origin = clock.GetUtcNow().AddHours(-1);
        RemoteExecutionRequest original = Request(fixture, origin.AddSeconds(5)) with { Admission = new(clock, TimeSpan.FromSeconds(5), origin.AddSeconds(5), origin) };
        clock.Advance(TimeSpan.FromSeconds(1));
        ClusterRequest cancellation = CoreService.Workflows.NodeCancellationBinding.CreateRequest(original, clock.GetUtcNow());
        NodeCancellationRequest body = cancellation.Payload.Json.Deserialize<NodeCancellationRequest>()!;
        Assert.Equal(TimeSpan.FromSeconds(4), body.StartBy - clock.GetUtcNow());
        Assert.Equal(original.Id, body.ExecutionId);
        Assert.Equal(original.TargetInstanceId, cancellation.TargetInstanceId);
        Assert.Equal(origin.AddSeconds(5), original.ExpiresAt);
    }

    /// <summary>元の実行開始期限まで取消を記憶し、UTC日時の変更では保持期間が変わらないことを確認します。</summary>
    /// <returns>時計差と時刻補正がある場合の取消を検証するタスクです。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task RemoteStartAndCancellationUseTheSameLocalAdmission(int offsetSeconds)
    {
        int calls = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ManagementTransport.AdmissionTestClock clock = new();
        DateTimeOffset sourceNow = clock.GetUtcNow();
        clock.Adjust(TimeSpan.FromSeconds(offsetSeconds));
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, timeProvider: clock);
        RemoteExecutionRequest request = Request(fixture, sourceNow.AddSeconds(5)) with { Admission = new(clock, TimeSpan.FromSeconds(5), sourceNow.AddSeconds(5), sourceNow) };
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request)).Outcome);
        RemoteExecutionRequest cancelled = request with { Id = Guid.NewGuid() };
        Assert.True(await executor.RequestCancellationAsync(cancelled.Id, cancelled.ExpiresAt, cancelled.Admission));
        clock.Adjust(TimeSpan.FromHours(2));
        NodeResult result = await executor.ExecuteAsync(cancelled);
        Assert.Equal("execution.cancelledBeforeStart", result.ReasonCode);
        clock.Advance(TimeSpan.FromSeconds(5));
        RemoteExecutionRequest expired = request with { Id = Guid.NewGuid() };
        Assert.Equal("execution.expired", (await executor.ExecuteAsync(expired)).ReasonCode);
        Assert.False(await executor.RequestCancellationAsync(expired.Id, expired.ExpiresAt, expired.Admission));
        Assert.Equal(1, calls);
    }

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
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await finish.Task.WaitAsync(token).ConfigureAwait(false);
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
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            clock.Advance(TimeSpan.FromHours(1));
            Assert.True(await executor.RequestCancellationAsync(request.Id, request.ExpiresAt));
            Assert.Equal(EOutcome.Cancelled, (await execution.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            // 取消を確かめる前に自然終了させず、試験が失敗した場合も処理を残しません。
            finish.TrySetResult();
            _ = await execution.ConfigureAwait(true);
        }
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
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await executor.RequestCancellationAsync(Guid.NewGuid(), clock.Now.AddMinutes(1)));
    }

    /// <summary>ローカル実体への一回要求です。</summary>
    /// <returns>明示した期限を持つ要求です。</returns>
    private static RemoteExecutionRequest Request(WorkflowFixture fixture, DateTimeOffset expires) => new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", WorkflowFixture.Definition("action"), new Dictionary<string, NodeValue>(), fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, expires);

    /// <summary>受付期限だけを確実に進めます。</summary>
    private sealed class TestClock : TimeProvider
    {
        private long elapsed;

        internal DateTimeOffset Now
        {
            get; private set;
        } = DateTimeOffset.UtcNow;

        /// <summary>試験の現在時刻です。</summary>
        /// <returns>制御されたUTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => Now;

        /// <summary>受付期間の経過を日時と独立して計測します。</summary>
        /// <returns>試験開始からの経過tick数です。</returns>
        public override long GetTimestamp() => elapsed;

        /// <summary>TimeSpanのtick単位を使用します。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>UTC日時と単調時計を同じ時間だけ進めます。</summary>
        internal void Advance(TimeSpan duration)
        {
            Now += duration;
            elapsed += duration.Ticks;
        }
    }
}
