using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>同じHost上の別Peerでも予約が共通であることを検証します。</summary>
[Collection("Performance")]
public sealed class ClusterFileGateTests
{
    /// <summary>別ファイルの同時予約が調停待ちになっても、通信期限で失敗させません。</summary>
    /// <returns>各要求の結果を保持し、全予約の取得と解放が成功することを確認します。</returns>
    [Theory]
    [InlineData(8, 600)]
    [InlineData(80, 31000)]
    public async Task ConcurrentReservationsWaitForTheirQueuedAdmissionsAsync(int count, int pauseMilliseconds)
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        DelayedGateRepository repository = new();
        GatePeer first = new(id, "first", port, true, repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await first.Cluster.StartAsync().ConfigureAwait(true);
        GatePeer second = new(id, "second", port, false, requestTimeout: TimeSpan.FromSeconds(1));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await second.Cluster.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        repository.Pause();
        Task<FileLeaseResult[]> acquiring = Task.WhenAll(Enumerable.Range(0, count).Select(index =>
            second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/queued-" + index + ".wav", true)], EFileConflictPolicy.Fail, timeout.Token)));
        await AssertWaitingAsync(repository, acquiring, timeout.Token, pauseMilliseconds).ConfigureAwait(true);
        FileLeaseResult[] acquired = await acquiring.ConfigureAwait(true);
        Assert.All(acquired, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        Assert.Equal(count, (await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token).ConfigureAwait(true)).Active.Count);
        FilePhaseResult[] released = await Task.WhenAll(acquired.Select(result => second.Gate.ReleaseAsync(result.Lease!, timeout.Token))).ConfigureAwait(true);
        Assert.All(released, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        Assert.InRange(repository.Preparations, count + 1, count * 2);
        Assert.Equal(count * 2, (await repository.ListAsync("receipts:first", timeout.Token).ConfigureAwait(true)).Count);
        Assert.Empty((await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token).ConfigureAwait(true)).Active);
    }

    /// <summary>取得と解放の配送期限を超えても、元操作だけを照会して結果を引き継ぎます。</summary>
    /// <returns>実UDPの遅延、確定失敗、予約数と永続受付件数の確認です。</returns>
    [Theory]
    [InlineData(false, 600)]
    [InlineData(true, 600)]
    [InlineData(false, 31000)]
    public async Task DelayedAdmissionAndReleaseUseTheOriginalResultAsync(bool reject, int pauseMilliseconds)
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        DelayedGateRepository repository = new() { Reject = reject };
        GatePeer first = new(id, "first", port, true, repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await first.Cluster.StartAsync().ConfigureAwait(true);
        GatePeer second = new(id, "second", port, false, requestTimeout: TimeSpan.FromMilliseconds(250));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await second.Cluster.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        repository.Pause();
        Task<FileLeaseResult> acquiring = second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/delayed.wav", true)], EFileConflictPolicy.Fail, timeout.Token);
        await AssertWaitingAsync(repository, acquiring, timeout.Token, pauseMilliseconds).ConfigureAwait(true);
        FileLeaseResult acquired = await acquiring.ConfigureAwait(true);
        Assert.Equal(reject ? EOutcome.Failed
                            : EOutcome.Succeeded, acquired.Outcome);
        Assert.Single(await repository.ListAsync("receipts:first", timeout.Token).ConfigureAwait(true));
        Assert.Equal(1, repository.Preparations);
        if (reject)
        {
            Assert.Equal("fixture.rejected", acquired.Reason);
            Assert.Null(await repository.ReadAsync("file:reservations", "active", timeout.Token).ConfigureAwait(true));
            return;
        }
        Assert.Single((await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token).ConfigureAwait(true)).Active);
        repository.Pause();
        Task<FilePhaseResult> releasing = second.Gate.ReleaseAsync(acquired.Lease!, timeout.Token);
        await AssertWaitingAsync(repository, releasing, timeout.Token, pauseMilliseconds).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await releasing.ConfigureAwait(true)).Outcome);
        Assert.Equal(2, repository.Preparations);
        Assert.Equal(2, (await repository.ListAsync("receipts:first", timeout.Token).ConfigureAwait(true)).Count);
        Assert.Empty((await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token).ConfigureAwait(true)).Active);
    }

    /// <summary>照会中の取消でも遅着する元予約を保持し、本体を始めず安全な後続解放へ渡します。</summary>
    /// <returns>所有情報の保持と、同じ取得の確定後だけの解放を確認します。</returns>
    [Fact]
    public async Task CancellationDuringConfirmationPreservesTheOriginalLeaseAsync()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        DelayedGateRepository repository = new();
        GatePeer first = new(id, "first", port, true, repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await first.Cluster.StartAsync().ConfigureAwait(true);
        GatePeer second = new(id, "second", port, false, requestTimeout: TimeSpan.FromMilliseconds(250));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await second.Cluster.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource caller = new();
        repository.Pause();
        Task<FileLeaseResult> acquiring = second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/cancelled.wav", true)], EFileConflictPolicy.Fail, caller.Token);
        FileLease lease;
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            await Task.Delay(600, timeout.Token).ConfigureAwait(true);
            await caller.CancelAsync().ConfigureAwait(true);
            FileLeaseResult cancelled = await acquiring.ConfigureAwait(true);
            Assert.Equal(EOutcome.Unknown, cancelled.Outcome);
            lease = Assert.IsType<FileLease>(cancelled.Lease);
            Assert.Equal(EOutcome.Unknown, (await second.Gate.ConfirmAsync(lease, timeout.Token).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            repository.Resume.TrySetResult();
            await Task.WhenAll(caller.CancelAsync(), acquiring).ConfigureAwait(true);
        }
        ClusterRequest original = new(lease.ReservationId, "file.gate.acquire", lease.Coordinator.PeerId, lease.Coordinator.InstanceId, lease.AdmissionExpiresAt, NodeValue.FromObject(lease));
        ClusterRequestStatus status;
        do
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            status = await second.Cluster.QueryRequestAsync(original, timeout.Token).ConfigureAwait(true);
        } while (status.Running);
        Assert.Equal(EOutcome.Succeeded, status.Response!.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await FileReservationRecovery.ReleaseEndedAsync(second.Gate, lease, false, timeout.Token).ConfigureAwait(true)).Outcome);
        Assert.Empty((await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token).ConfigureAwait(true)).Active);
        Assert.Equal(2, repository.Preparations);
    }

    /// <summary>短い通信期限後も副作用を再発行せず、読取り照会で待っていることを確認します。</summary>
    /// <returns>元の保存を再開できる状態です。</returns>
    private static async Task AssertWaitingAsync(DelayedGateRepository repository, Task operation, CancellationToken cancellationToken, int pauseMilliseconds = 600)
    {
        try
        {
            await repository.Entered.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            await Task.Delay(pauseMilliseconds, cancellationToken).ConfigureAwait(true);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            repository.Resume.TrySetResult();

            // 結果は呼出側で検証し、途中失敗時も要求が終わるまでPeerを保持する。
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>未実行ノードの予約でも、遅れて届く元取得が確定するまで解放しません。</summary>
    /// <returns>二つの実Peerで、取得遅延と次回競合時の安全な自動解放を確認します。</returns>
    [Fact]
    public async Task UnusedAdmissionIsRecoveredOnlyAfterTheOriginalReservationCommitsAsync()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        GatePeer first = new(id, "first", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await first.Cluster.StartAsync().ConfigureAwait(true);
        GatePeer second = new(id, "second", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await second.Cluster.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        PeerInfo[] members = second.Cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        PeerInfo coordinator = members.Single(peer => string.Equals(peer.PeerId, second.Cluster.CoordinatorId, StringComparison.Ordinal));
        FileLease lease = new(Guid.NewGuid(), "second", second.Cluster.LocalInstanceId, coordinator, members, [Access(second, "/delayed.wav", true)])
        {
            ReservationId = Guid.NewGuid(),
            AdmissionExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        NodeResult execution = NodeResult.Failure("Cluster.ResponseUnconfirmed", "Admission response was not received.", EOutcome.Unknown);
        FilePhaseResult release = new(EOutcome.NotExecuted, "File.ReservationRetained");
        string scope = "peer:second:file-recovery", key = lease.Id.ToString("N");
        await second.Repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, null,
            NodeValue.FromObject(new { lease, execution, release, reservationConfirmed = false, fileExecutionStarted = false }))]).ConfigureAwait(true);
        Assert.False(await FileReservationRecovery.ReleaseCompletedAsync("second", second.Repository, second.Gate, lease.Footprints, false).ConfigureAwait(true));
        Assert.Null(await first.Repository.ReadAsync("file:reservations", "active").ConfigureAwait(true));
        ClusterRequest original = new(lease.ReservationId, "file.gate.acquire", coordinator.PeerId, coordinator.InstanceId, lease.AdmissionExpiresAt, NodeValue.FromObject(lease));
        ClusterResponse response = await second.Cluster.RequestAsync(original).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await RemoteNodeCompletion.ConfirmAsync(second.Cluster, original, response).ConfigureAwait(true)).Outcome);
        FileLeaseResult next = await second.Gate.AcquireAsync(Guid.NewGuid(), lease.Footprints, EFileConflictPolicy.Fail).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
        JsonElement stored = (await second.Repository.ReadAsync(scope, key).ConfigureAwait(true))!.Value!.Json;
        Assert.Equal((int)EOutcome.Unknown, stored.GetProperty("execution").GetProperty("Outcome").GetInt32());
        Assert.Equal((int)EOutcome.Succeeded, stored.GetProperty("release").GetProperty("Outcome").GetInt32());
        Assert.False(stored.GetProperty("fileExecutionStarted").GetBoolean());
        Assert.Single((await FileReservationLedger.ReadAsync(first.Repository, "file:reservations").ConfigureAwait(true)).Active);
        Assert.Equal(EOutcome.Succeeded, (await second.Gate.ReleaseAsync(next.Lease!).ConfigureAwait(true)).Outcome);
    }

    /// <summary>解放ACKを失った完了予約だけを回収し、不明な処理は排他を保持します。</summary>
    /// <returns>二実体の予約状態と、元の実行結果を保持した復旧記録の確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded, false)]
    [InlineData(EOutcome.Failed, false)]
    [InlineData(EOutcome.NotExecuted, false)]
    [InlineData(EOutcome.Cancelled, false)]
    [InlineData(EOutcome.Unknown, false)]
    [InlineData(EOutcome.Unknown, true)]
    public async Task OverlapRecoversOnlyDurablyCompletedLocalExecutions(EOutcome outcome, bool fileAccessCompleted)
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        GatePeer first = new(id, "first", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await first.Cluster.StartAsync();
        GatePeer second = new(id, "second", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        await second.Cluster.StartAsync();
        await ReadyAsync(first, second);
        FileLeaseResult held = await first.Gate.AcquireAsync(Guid.NewGuid(), [Access(first, "/G01.png", true)], EFileConflictPolicy.Fail);
        Assert.Equal(EOutcome.Succeeded, held.Outcome);
        NodeResult execution = NodeResult.Success(NodeValue.FromObject("original result")) with { Outcome = outcome, FileAccessCompleted = fileAccessCompleted };
        FilePhaseResult release = new(EOutcome.Unknown, "Replication.RequiredAcknowledgementMissing");
        string scope = "peer:first:file-recovery", key = held.Lease!.Id.ToString("N");
        await first.Repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, null, NodeValue.FromObject(new { lease = held.Lease, execution, release }))]);
        Assert.Equal("File.Overlap", (await second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/G01.png", true)], EFileConflictPolicy.Fail)).Reason);
        FileLeaseResult next = await first.Gate.AcquireAsync(Guid.NewGuid(), [Access(first, "/G01.png", true)], EFileConflictPolicy.Fail);
        bool completed = outcome != EOutcome.Unknown || fileAccessCompleted;
        Assert.Equal(completed ? EOutcome.Succeeded
                               : EOutcome.NotExecuted, next.Outcome);
        JsonElement recovery = (await first.Repository.ReadAsync(scope, key))!.Value!.Json;
        Assert.Equal((int)outcome, recovery.GetProperty("execution").GetProperty("Outcome").GetInt32());
        Assert.Equal("original result", recovery.GetProperty("execution").GetProperty("Outputs").GetProperty("value").GetProperty("Json").GetString());
        Assert.Equal(completed ? (int)EOutcome.Succeeded
                               : (int)EOutcome.Unknown, recovery.GetProperty("release").GetProperty("Outcome").GetInt32());
        Assert.Single((await FileReservationLedger.ReadAsync(first.Repository, "file:reservations")).Active);
        if (completed)
        {
            Assert.Equal(EOutcome.Succeeded, (await first.Gate.ReleaseAsync(next.Lease!)).Outcome);
        }
    }

    /// <summary>read/read併存、包含write排他、複数パス原子性とWait取消を確認します。</summary>
    /// <returns>実UDP検証完了。</returns>
    [Fact]
    public async Task SharedHostOverlapIsAtomicAndWaitingCanBeCancelled()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        GatePeer first = new(id, "first", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await first.Cluster.StartAsync();
        GatePeer second = new(id, "second", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        await second.Cluster.StartAsync();
        await ReadyAsync(first, second);
        FileLeaseResult firstRead = await first.Gate.AcquireAsync(Guid.NewGuid(), [Access(first, "/tree", false)], EFileConflictPolicy.Fail);
        FileLeaseResult secondRead = await second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/tree/child", false)], EFileConflictPolicy.Fail);
        Assert.Equal(EOutcome.Succeeded, firstRead.Outcome);
        Assert.Equal(EOutcome.Succeeded, secondRead.Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/tree", true), Access(second, "/other", true)], EFileConflictPolicy.Fail)).Outcome);
        FileLeaseResult other = await first.Gate.AcquireAsync(Guid.NewGuid(), [Access(first, "/other", true)], EFileConflictPolicy.Fail);
        Assert.Equal(EOutcome.Succeeded, other.Outcome);
        string receiptsScope = "receipts:first";
        int receiptsBeforeWait = (await first.Repository.ListAsync(receiptsScope)).Count;
        using CancellationTokenSource waiting = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Task<FileLeaseResult> acquiring = second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/tree/child/grandchild", true)], EFileConflictPolicy.Wait, waiting.Token);
        try
        {
            while ((await first.Repository.ListAsync(receiptsScope, deadline.Token)).Count == receiptsBeforeWait)
            {
                await Task.Delay(10, deadline.Token);
            }
            Assert.False(acquiring.IsCompleted);
            await waiting.CancelAsync();
            FileLeaseResult cancelled = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(EOutcome.Unknown, cancelled.Outcome);
            Assert.True(waiting.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
            Assert.True(waiting.IsCancellationRequested);
        }
        finally
        {
            await waiting.CancelAsync();
            await ((Task)acquiring).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        Assert.Equal(receiptsBeforeWait + 1, (await first.Repository.ListAsync("receipts:first")).Count);
        Assert.Equal(3, (await FileReservationLedger.ReadAsync(first.Repository, "file:reservations")).Active.Count);
        Assert.Equal(EOutcome.Succeeded, (await first.Gate.ReleaseAsync(firstRead.Lease!)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await second.Gate.ReleaseAsync(secondRead.Lease!)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await first.Gate.ReleaseAsync(other.Lease!)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/tree", true)], EFileConflictPolicy.Fail)).Outcome);
    }

    /// <summary>所有Peerが失われても未知の予約を期限で除去しません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task MissingOwnerDoesNotExpireOrPermitWrongOwnerRelease()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        GatePeer first = new(id, "first", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await first.Cluster.StartAsync();
        GatePeer second = new(id, "second", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        await second.Cluster.StartAsync();
        await ReadyAsync(first, second);
        FileLeaseResult held = await second.Gate.AcquireAsync(Guid.NewGuid(), [Access(second, "/unknown", true)], EFileConflictPolicy.Fail);
        Assert.Equal(EOutcome.Succeeded, held.Outcome);
        Assert.Equal("File.LeaseOwnerMismatch", (await first.Gate.ReleaseAsync(held.Lease! with { OwnerInstanceId = Guid.NewGuid() })).Reason);
        await second.Cluster.StopAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (first.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 1)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal("File.Overlap", (await first.Gate.AcquireAsync(Guid.NewGuid(), [Access(first, "/unknown/child", true)], EFileConflictPolicy.Fail)).Reason);
        Assert.Single((await FileReservationLedger.ReadAsync(first.Repository, "file:reservations")).Active);
    }

    /// <summary>所有実体が同じ物理Hostに属する範囲です。</summary>
    /// <returns>検証済み範囲を模した値。</returns>
    private static FileFootprint Access(GatePeer peer, string path, bool write) => new(peer.Cluster.LocalPeerId, peer.Cluster.LocalInstanceId, "same-host", path, write);

    /// <summary>必要な両実体の同期を確認します。</summary>
    /// <returns>Ready成立。</returns>
    private static async Task ReadyAsync(GatePeer first, GatePeer second)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (first.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>独立した試験ポートです。</summary>
    /// <returns>UDP port。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>同一Host名で独立保存・独立UDP実体を構成します。</summary>
    private sealed class GatePeer : IAsyncDisposable
    {
        /// <summary>共通調停だけを起動します。</summary>
        public GatePeer(string id, string peer, int port, bool fresh, IDurableRepository? repository = null, TimeSpan? requestTimeout = null)
        {
            Repository = repository ?? new SqliteRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
            Cluster = new PeerCluster(new ClusterOptions(id, peer, "same-host", "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath()) { HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2) }, Repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
            Gate = new ClusterFileGate(Cluster, Repository);
        }

        /// <summary>独立保存先です。</summary>
        public IDurableRepository Repository
        {
            get;
        }

        /// <summary>独立した参加実体です。</summary>
        public PeerCluster Cluster
        {
            get;
        }

        /// <summary>クラスタ共通予約です。</summary>
        public ClusterFileGate Gate
        {
            get;
        }

        /// <summary>実タスクの終了後に調停資源を解放します。</summary>
        /// <returns>終了完了。</returns>
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Cluster).DisposeAsync().ConfigureAwait(false);
            Gate.Dispose();
        }
    }
}
