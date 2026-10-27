using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>異なる受信Peerが実通信で同じ枠と待機順を共有することを確認します。</summary>
[Collection("TimingSensitive")]
public sealed class ClusterConcurrencyTests
{
    /// <summary>別PCからの待機が同じ一覧に現れ、前の区間が終わるまで開始しません。</summary>
    /// <returns>共有上限、待機表示、解放後の進行です。</returns>
    [Fact]
    public async Task DifferentPeersShareOneLimitAndReleaseQueueAsync()
    {
        Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        WorkflowConcurrencyAdmission first = await fixture.FirstLimit.AcquireAsync(Fixture.Request(fixture.First), timeout.Token).ConfigureAwait(true);
        Assert.Null(first.Rejection);
        Assert.NotNull(first.Lease);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstCleanup = first.Lease.ConfigureAwait(true);
        WorkflowConcurrencyRequest waiting = Fixture.Request(fixture.Second);
        Task<WorkflowConcurrencyAdmission> pending = fixture.SecondLimit.AcquireAsync(waiting, timeout.Token);
        await fixture.WaitAsync(snapshot => snapshot.WaitingCount == 1, timeout.Token).ConfigureAwait(true);
        WorkflowConcurrencySnapshot snapshot = await fixture.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, snapshot.ActiveCount);
        Assert.Equal(waiting.TicketId, Assert.Single(snapshot.Waiting).TicketId);
        Assert.Equal(fixture.Second.LocalPeerId, snapshot.Waiting[0].OwnerPeerId);
        Assert.False(pending.IsCompleted);
        await first.Lease.DisposeAsync().ConfigureAwait(true);
        WorkflowConcurrencyAdmission next = await pending.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.Null(next.Rejection);
        Assert.NotNull(next.Lease);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable nextCleanup = next.Lease.ConfigureAwait(true);
        await next.Lease.DisposeAsync().ConfigureAwait(true);
        await fixture.WaitAsync(value => value.ActiveCount == 0 && value.WaitingCount == 0, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>待機の取消は次の到達を進め、表示の反復取得は占有行を更新しません。</summary>
    /// <returns>FIFO、取消対象の除去、待機中の保存回数ゼロです。</returns>
    [Fact]
    public async Task WaitingCancellationPreservesQueueOrderWithoutPollingWritesAsync()
    {
        Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        WorkflowConcurrencyAdmission first = await fixture.FirstLimit.AcquireAsync(Fixture.Request(fixture.First), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(first.Lease);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstCleanup = first.Lease.ConfigureAwait(true);
        string key = ContractJson.Hash(new WorkflowConcurrencyTarget("workflow", "limit"));
        Infrastructure.Persistence.DurableEntry? before = await fixture.FirstRepository.ReadAsync("execution:concurrency:permits", key, timeout.Token).ConfigureAwait(true);
        Task<WorkflowConcurrencyAdmission> leaving = fixture.SecondLimit.AcquireAsync(Fixture.Request(fixture.Second) with { WaitTimeoutMilliseconds = 0 }, cancelled.Token);
        await fixture.WaitAsync(value => value.WaitingCount == 1, timeout.Token).ConfigureAwait(true);
        WorkflowConcurrencyRequest following = Fixture.Request(fixture.First) with { WaitTimeoutMilliseconds = 0 };
        Task<WorkflowConcurrencyAdmission> waiting = fixture.FirstLimit.AcquireAsync(following, timeout.Token);
        await fixture.WaitAsync(value => value.WaitingCount == 2, timeout.Token).ConfigureAwait(true);
        for (int index = 0; index < 10; index++)
        {
            Assert.Equal(2, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).WaitingCount);
        }
        Infrastructure.Persistence.DurableEntry? after = await fixture.FirstRepository.ReadAsync("execution:concurrency:permits", key, timeout.Token).ConfigureAwait(true);
        Assert.Equal(before!.Version, after!.Version);
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving.WaitAsync(timeout.Token)).ConfigureAwait(true);
        await fixture.WaitAsync(value => value.WaitingCount == 1, timeout.Token).ConfigureAwait(true);

        // 取消は元の枠の解放を待たず完了し、次の待機者は実際に空くまで開始しません。
        WorkflowConcurrencySnapshot remaining = await fixture.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, remaining.ActiveCount);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(following.TicketId, Assert.Single(remaining.Waiting).TicketId);
        await first.Lease.DisposeAsync().ConfigureAwait(true);
        WorkflowConcurrencyAdmission last = await waiting.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.NotNull(last.Lease);
        await last.Lease.DisposeAsync().ConfigureAwait(true);
        Assert.Equal(0, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).WaitingCount);
    }

    /// <summary>担当Coreの交代後も、別Peerが保持している枠を新たな空きと解釈しません。</summary>
    /// <returns>共有済み占有の復元と、元所有者による新担当への解放です。</returns>
    [Fact]
    public async Task CoordinatorChangePreservesConfirmedActivePermitAsync()
    {
        Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        Assert.Equal(fixture.First.LocalPeerId, fixture.Second.CoordinatorId);
        WorkflowConcurrencyAdmission first = await fixture.SecondLimit.AcquireAsync(Fixture.Request(fixture.Second), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(first.Lease);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstCleanup = first.Lease.ConfigureAwait(true);
        await fixture.First.StopAsync(timeout.Token).ConfigureAwait(true);
        while (!string.Equals(fixture.Second.CoordinatorId, fixture.Second.LocalPeerId, StringComparison.Ordinal))
        {
            await Task.Delay(30, timeout.Token).ConfigureAwait(true);
        }
        WorkflowConcurrencyAdmission blocked = await fixture.SecondLimit.AcquireAsync(Fixture.Request(fixture.Second) with { WaitTimeoutMilliseconds = 150 }, timeout.Token).ConfigureAwait(true);
        Assert.Null(blocked.Lease);
        Assert.Equal("concurrency.timeout", blocked.Rejection?.ReasonCode);
        Assert.Equal(1, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).ActiveCount);
        await first.Lease.DisposeAsync().ConfigureAwait(true);
        WorkflowConcurrencyAdmission next = await fixture.SecondLimit.AcquireAsync(Fixture.Request(fixture.Second), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(next.Lease);
        await next.Lease.DisposeAsync().ConfigureAwait(true);
        Assert.Equal(0, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).ActiveCount);
    }

    /// <summary>期限0の待機は配送受付期限を過ぎても完了照会を続け、二重開始しません。</summary>
    /// <returns>30秒の受付期限より長い待機後の一度だけの取得です。</returns>
    [Fact]
    public async Task UnlimitedWaitSurvivesTransportAdmissionDeadlineAsync()
    {
        Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(50));
        WorkflowConcurrencyAdmission first = await fixture.FirstLimit.AcquireAsync(Fixture.Request(fixture.First), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(first.Lease);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstCleanup = first.Lease.ConfigureAwait(true);
        Task<WorkflowConcurrencyAdmission> waiting = fixture.SecondLimit.AcquireAsync(Fixture.Request(fixture.Second) with { WaitTimeoutMilliseconds = 0 }, timeout.Token);
        await fixture.WaitAsync(value => value.WaitingCount == 1, timeout.Token).ConfigureAwait(true);
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(31);
        while (!waiting.IsCompleted && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(100, timeout.Token).ConfigureAwait(true);
        }
        if (waiting.IsCompleted)
        {
            WorkflowConcurrencyAdmission early = await waiting.ConfigureAwait(true);
            Assert.Fail(System.Text.Json.JsonSerializer.Serialize(early.Rejection));
        }
        Assert.Null(Assert.Single((await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).Waiting).Deadline);
        await first.Lease.DisposeAsync().ConfigureAwait(true);
        WorkflowConcurrencyAdmission next = await waiting.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.NotNull(next.Lease);
        await next.Lease.DisposeAsync().ConfigureAwait(true);
        Assert.Equal(0, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).ActiveCount);
    }

    /// <summary>待機期限は失敗と強制進行を区別し、強制進行も占有数へ含めます。</summary>
    /// <returns>期限後の挙動と、解放後に現役枠を残さない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutEitherFailsOrCountsForcedSessionAsync(bool proceed)
    {
        Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        WorkflowConcurrencyAdmission first = await fixture.FirstLimit.AcquireAsync(Fixture.Request(fixture.First), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(first.Lease);
        try
        {
            WorkflowConcurrencyAdmission second = await fixture.SecondLimit.AcquireAsync(Fixture.Request(fixture.Second) with
            {
                WaitTimeoutMilliseconds = 80, ProceedOnTimeout = proceed,
            }, timeout.Token).ConfigureAwait(true);
            if (proceed)
            {
                Assert.Null(second.Rejection);
                Assert.True(second.Forced);
                Assert.NotNull(second.Lease);
                Assert.Equal(2, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).ActiveCount);
                await second.Lease.DisposeAsync().ConfigureAwait(true);
            }
            else
            {
                Assert.Null(second.Lease);
                Assert.Equal(EOutcome.Failed, second.Rejection?.Outcome);
                Assert.Equal("concurrency.timeout", second.Rejection?.ReasonCode);
                Assert.Equal(1, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).ActiveCount);
            }
            Assert.Equal(0, (await fixture.ReadAsync(timeout.Token).ConfigureAwait(true)).WaitingCount);
        }
        finally
        {
            await first.Lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>本番と分離した二つのPeerと実保存を所有します。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>ランダムなクラスタとloopbackポートだけを使用します。</summary>
        private Fixture()
        {
            string cluster = "concurrency-" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(Path.GetTempPath(), cluster);
            using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            SqliteRepository left = FirstRepository = new(Path.Combine(path, "first.db"));
            SqliteRepository right = new(Path.Combine(path, "second.db"));
            First = Create(cluster, "first", port, true, left);
            Second = Create(cluster, "second", port, false, right);
            FirstLimit = new(First, left);
            SecondLimit = new(Second, right);
        }

        internal PeerCluster First
        {
            get;
        }

        internal SqliteRepository FirstRepository
        {
            get;
        }

        internal PeerCluster Second
        {
            get;
        }

        internal ClusterWorkflowConcurrencyLimiter FirstLimit
        {
            get;
        }

        internal ClusterWorkflowConcurrencyLimiter SecondLimit
        {
            get;
        }

        /// <summary>新しい独立した実行の到達を作ります。</summary>
        /// <returns>入力本文を含まない制限要求です。</returns>
        internal static WorkflowConcurrencyRequest Request(PeerCluster peer) => new(Guid.NewGuid(), "workflow", "limit", Guid.NewGuid(), peer.LocalPeerId, peer.LocalInstanceId, 1, 10000, false);

        /// <summary>実際の照会経路で同じノードの一覧を確認します。</summary>
        /// <returns>一つのノードの現在表示です。</returns>
        internal async Task<WorkflowConcurrencySnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Assert.Single(await SecondLimit.ReadAsync([new("workflow", "limit")], cancellationToken).ConfigureAwait(false));

        /// <summary>変化した条件を期限付きで確認します。</summary>
        /// <returns>待機順または占有数の確認です。</returns>
        internal async Task WaitAsync(Func<WorkflowConcurrencySnapshot, bool> condition, CancellationToken cancellationToken = default)
        {
            while (!condition(await ReadAsync(cancellationToken).ConfigureAwait(false)))
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>保存・発見・通常参加を完了してから試験へ渡します。</summary>
        /// <returns>終了処理を所有する隔離構成です。</returns>
        // 抑制理由: 準備に失敗した構成だけを片付け、元の試験失敗を置換せず呼出元へ戻します。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "準備失敗時の資源回収後、元の原因とスタックを試験へ保持します。")]
        internal static async Task<Fixture> CreateAsync(CancellationToken cancellationToken = default)
        {
            Fixture fixture = new();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await fixture.First.StartAsync(timeout.Token).ConfigureAwait(false);
                await fixture.Second.StartAsync(timeout.Token).ConfigureAwait(false);
                while (fixture.First.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || fixture.Second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>保存先と発見範囲が共有される試験Peerです。</summary>
        /// <returns>通常参加条件を満たすPeerです。</returns>
        private static PeerCluster Create(string cluster, string name, int port, bool initialize, SqliteRepository repository) => new(
            new ClusterOptions(cluster, name, name, "127.0.0.1", "127.255.255.255", port, 1, initialize, Path.GetTempPath())
            { HeartbeatInterval = TimeSpan.FromMilliseconds(30), RequestTimeout = TimeSpan.FromSeconds(3) }, repository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };

        /// <summary>終了通知に使う通信を止める前に、区間管理を回収します。</summary>
        /// <returns>全所有資源の終了です。</returns>
        public async ValueTask DisposeAsync()
        {
            await SecondLimit.DisposeAsync().ConfigureAwait(false);
            await FirstLimit.DisposeAsync().ConfigureAwait(false);
            await ((IAsyncDisposable)Second).DisposeAsync().ConfigureAwait(false);
            await ((IAsyncDisposable)First).DisposeAsync().ConfigureAwait(false);
        }
    }
}
