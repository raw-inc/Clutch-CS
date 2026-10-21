using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実時間の長い待機を使わず、欠落・復帰・再欠落と終了時の回収を確認します。</summary>
public sealed class SignalWatchdogTests
{
    /// <summary>初回未受信でも発火し、欠落中には繰り返さず、復帰後は別の範囲を作ります。</summary>
    /// <returns>二回の退避と、それぞれの終了確認です。</returns>
    [Fact]
    public async Task MissingFirstHeartbeatStartsOnceAndRecoveryRearmsAsync()
    {
        Fixture fixture = new();
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        await fixture.WaitAsync(() => fixture.Clock.Pending == 1).ConfigureAwait(true);
        fixture.Clock.Advance(99);
        Assert.Empty(fixture.Emissions);
        fixture.Clock.Advance(1);
        await fixture.WaitAsync(() => fixture.Emissions.Count == 1).ConfigureAwait(true);
        SourceScope first = Assert.Single(Assert.Single(fixture.Emissions).Context.SourceScopes!);
        fixture.Clock.Advance(10000);
        Assert.Single(fixture.Emissions);
        fixture.Pulse();
        await fixture.WaitAsync(() => fixture.Scopes.Ended.Count == 1 && fixture.Clock.Pending == 1).ConfigureAwait(true);
        Assert.Equal(first, Assert.Single(fixture.Scopes.Ended));
        fixture.Clock.Advance(99);
        Assert.Single(fixture.Emissions);
        fixture.Clock.Advance(1);
        await fixture.WaitAsync(() => fixture.Emissions.Count == 2).ConfigureAwait(true);
        SourceScope second = Assert.Single(fixture.Emissions.Last().Context.SourceScopes!);
        Assert.NotEqual(first.Id, second.Id);
        await fixture.Stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Completion.ConfigureAwait(true)).Outcome);
        Assert.Equal(2, fixture.Scopes.Ended.Count);
        Assert.Equal(0, fixture.Clock.Pending);
    }

    /// <summary>受信頻度が高くても最新時刻一件だけを保持し、タイマーを残しません。</summary>
    /// <returns>大量通知後の再計時と監視終了の確認です。</returns>
    [Fact]
    public async Task PulseBurstKeepsOnePendingSignalAndTimerAsync()
    {
        Fixture fixture = new();
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        await fixture.WaitAsync(() => fixture.Clock.Pending == 1).ConfigureAwait(true);
        for (int wave = 0; wave < 10; wave++)
        {
            fixture.Clock.Advance(50);
            for (int index = 0; index < 1000; index++)
            {
                fixture.Pulse();
                Assert.InRange(fixture.Signals.Reader.Count, 0, 1);
            }
            await fixture.WaitAsync(() => fixture.Signals.Reader.Count == 0 && fixture.Clock.Pending == 1).ConfigureAwait(true);
            Assert.Empty(fixture.Emissions);
        }
        await fixture.Stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Completion.ConfigureAwait(true)).Outcome);
        Assert.Equal(0, fixture.Clock.Pending);
        Assert.Empty(fixture.Scopes.Ended);
    }

    /// <summary>終了未確認を正常終了で隠さず、元の範囲の終了は一度だけ要求します。</summary>
    /// <returns>予定停止・緊急取消と、下流の確認失敗の区別です。</returns>
    [Theory]
    [InlineData(false, false, EOutcome.Succeeded)]
    [InlineData(true, false, EOutcome.Cancelled)]
    [InlineData(false, true, EOutcome.Unknown)]
    public async Task StopReleasesFallbackAndPreservesUnconfirmedOutcomeAsync(bool cancelled, bool unconfirmed, EOutcome expected)
    {
        Fixture fixture = new();
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        if (unconfirmed)
        {
            fixture.Scopes.EndResult = NodeResult.Failure("source.scope.stopUnconfirmed", "test", EOutcome.Unknown);
        }
        await fixture.WaitAsync(() => fixture.Clock.Pending == 1).ConfigureAwait(true);
        fixture.Clock.Advance(100);
        await fixture.WaitAsync(() => fixture.Emissions.Count == 1).ConfigureAwait(true);
        CancellationTokenSource stop = fixture.Stop;
        if (cancelled)
        {
            stop = fixture.Cancel;
        }
        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(expected, (await fixture.Completion.ConfigureAwait(true)).Outcome);
        Assert.Single(fixture.Scopes.Ended);
        Assert.Equal(0, fixture.Clock.Pending);
    }

    /// <summary>複数監視PCや受信元依存の選択を許可せず、時間は正の有限値を要求します。</summary>
    [Fact]
    public void SettingsRequirePositiveTimeoutAndOneMonitoringPeer()
    {
        SignalWatchdogNode node = new();
        NodeDefinition definition = new("watch", node.Descriptor.Type, NodeSettings.Schema("{}"));
        Assert.Empty(node.Validate(definition));
        Assert.NotEmpty(node.Validate(definition with { Settings = NodeSettings.Schema("{\"timeoutMs\":0}") }));
        Assert.NotEmpty(node.Validate(definition with { Target = new(EExecutionScope.All, ["a", "b"]) }));
        Assert.NotEmpty(node.Validate(definition with { Target = new(EExecutionScope.One, SourcePeer: true) }));
        Assert.NotEmpty(node.Validate(definition with { PcExecution = Contracts.Targets.EPcExecutionMode.All }));
    }

    /// <summary>一回の監視に必要な有限資源を所有します。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        internal IterationTestClock Clock
        {
            get;
        } = new();

        internal CancellationTokenSource Stop
        {
            get;
        } = new();

        internal CancellationTokenSource Cancel
        {
            get;
        } = new();

        internal Channel<long> Signals
        {
            get;
        } = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

        internal ConcurrentQueue<NodeEmission> Emissions
        {
            get;
        } = new();

        internal ScopeControl Scopes
        {
            get;
        } = new();

        internal Task<NodeResult> Completion
        {
            get;
        }

        /// <summary>一つの実Sourceを、単調時計と確認可能な終了境界へ接続します。</summary>
        internal Fixture()
        {
            NodeExecutionContext initial = new(Guid.NewGuid(), Guid.NewGuid(), "w", "revision", "watch", "a", NodeSettings.Schema("{\"timeoutMs\":100}"),
                new Dictionary<string, NodeValue>(), Clock);
            NodeSourceContext context = new(initial, () => initial with { ExecutionId = Guid.NewGuid(), CausationId = Guid.NewGuid() }, (emission, _) =>
            {
                Emissions.Enqueue(emission);
                return ValueTask.CompletedTask;
            }, () => Stop.IsCancellationRequested, stopToken: Stop.Token) { Signals = Signals.Reader, Scopes = Scopes };
            Completion = new SignalWatchdogNode().RunAsync(context, Cancel.Token);
        }

        /// <summary>本文を持たない最新の到着を通知します。</summary>
        internal void Pulse() => Assert.True(Signals.Writer.TryWrite(Clock.GetTimestamp()));

        /// <summary>偽時計の進行とは独立に、非同期継続が観測条件を満たすまで有限時間だけ待ちます。</summary>
        /// <returns>試験条件の成立です。</returns>
        internal async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                if (Completion.IsCompleted)
                {
                    _ = await Completion.ConfigureAwait(false);
                    Assert.True(condition(), "The watchdog completed before the expected transition.");
                    return;
                }
                await Task.Delay(1, timeout.Token).ConfigureAwait(false);
            }
        }

        /// <summary>試験に失敗しても監視とタイマーを残しません。</summary>
        /// <returns>実監視の終了です。</returns>
        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync().ConfigureAwait(false);
            _ = await Completion.ConfigureAwait(false);
            Stop.Dispose();
            Cancel.Dispose();
        }
    }

    /// <summary>範囲の固有性と終了要求を記録する、保存を持たない境界です。</summary>
    private sealed class ScopeControl : ISourceScopeControl
    {
        internal ConcurrentQueue<SourceScope> Ended
        {
            get;
        } = new();

        internal NodeResult EndResult
        {
            get; set;
        } = NodeResult.Success();

        /// <summary>別の退避回と共有しないIDを作ります。</summary>
        /// <returns>この試験の所有範囲です。</returns>
        public SourceScope Create(string workflowId) => new(Guid.NewGuid(), workflowId, "a", Guid.NewGuid());

        /// <summary>ノード単体試験では他Peerへの登録を行いません。</summary>
        /// <returns>利用可能な所有元です。</returns>
        public ValueTask<bool?> CheckAsync(SourceScope scope, bool enroll, CancellationToken cancellationToken = default) => ValueTask.FromResult<bool?>(true);

        /// <summary>範囲ごとの終了要求と設定した確認結果を返します。</summary>
        /// <returns>正常終了または意図した確認失敗です。</returns>
        public ValueTask<NodeResult> EndAsync(SourceScope scope, CancellationToken cancellationToken = default)
        {
            Ended.Enqueue(scope);
            return ValueTask.FromResult(EndResult);
        }
    }
}
