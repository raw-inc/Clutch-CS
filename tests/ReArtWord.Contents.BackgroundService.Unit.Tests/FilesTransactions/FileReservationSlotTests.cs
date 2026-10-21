using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>予約数に応じた再利用と、旧版への切替でも保持中の排他が残ることを確認します。</summary>
[Collection("Performance")]
public sealed class FileReservationSlotTests
{
    /// <summary>総実行数が増えてもスロット数を増やさず、各変更の履歴を一予約分に収めます。</summary>
    /// <returns>包含パスの排他、変更本文の上限、スロット再利用の確認です。</returns>
    [Fact]
    public async Task RepeatedWavesReuseSlotsAndStoreOnlyTheChangedReservationAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        SlotPeer peer = new(repository, Guid.NewGuid().ToString("N"), "one", LargeSynchronizationTests.Port(), 1, true, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        for (int wave = 0; wave < 4; wave++)
        {
            List<FileLease> leases = [];
            for (int index = 0; index < 12; index++)
            {
                FileLeaseResult result = await peer.Gate!.AcquireAsync(Guid.CreateVersion7(), [Access(peer, "/tree/" + index)], EFileConflictPolicy.Fail).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                leases.Add(result.Lease!);
                DurableReceipt receipt = (await repository.ReadReceiptAsync(result.Lease!.ReservationId).ConfigureAwait(true))!;
                Assert.Equal(2, receipt.Entries.Count);
                Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(receipt).Length, 1, 8192);
            }
            Assert.Equal(EOutcome.NotExecuted, (await peer.Gate!.AcquireAsync(Guid.CreateVersion7(), [Access(peer, "/tree")], EFileConflictPolicy.Fail).ConfigureAwait(true)).Outcome);
            foreach (FileLease lease in leases)
            {
                Assert.Equal(EOutcome.Succeeded, (await peer.Gate.ReleaseAsync(lease).ConfigureAwait(true)).Outcome);
            }
            IReadOnlyList<DurableEntry> slots = await repository.ListAsync("file:reservations:slots").ConfigureAwait(true);
            Assert.Equal(12, slots.Count);
            Assert.All(slots, entry => Assert.Null(entry.Value));
            Assert.Empty((await FileReservationLedger.ReadAsync(repository, "file:reservations").ConfigureAwait(true)).Active);
        }
    }

    /// <summary>旧版が混在する間は旧形式を維持し、更新後に未解放予約ごと移して旧Coordinatorへの復帰にも戻します。</summary>
    /// <returns>予約の消失・二重取得を起こさない双方向の形式切替です。</returns>
    [Fact]
    public async Task MixedVersionsPreserveHeldReservationsThroughUpgradeAndDowngradeAsync()
    {
        using MediaTestFiles files = new();
        string id = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        SqliteRepository firstStore = new(Path.Combine(files.Root, "first.db")), secondStore = new(Path.Combine(files.Root, "second.db"));
        SlotPeer first = new(firstStore, id, "first", port, 10, true, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await first.Cluster.StartAsync().ConfigureAwait(true);
        FileLease? original;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        SlotPeer oldSecond = new(secondStore, id, "second", port, 1, false, false);
        await using (System.Runtime.CompilerServices.ConfiguredAsyncDisposable oldLifetime = oldSecond.ConfigureAwait(true))
        {
            await oldSecond.Cluster.StartAsync().ConfigureAwait(true);
            await ReadyAsync(first, oldSecond, timeout.Token).ConfigureAwait(true);
            FileLeaseResult held = await first.Gate!.AcquireAsync(Guid.CreateVersion7(), [Access(first, "/held")], EFileConflictPolicy.Fail, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, held.Outcome);
            original = held.Lease;
            Assert.False((await FileReservationLedger.ReadAsync(firstStore, "file:reservations", timeout.Token).ConfigureAwait(true)).Slotted);
        }
        SlotPeer second = new(secondStore, id, "second", port, 1, false, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await second.Cluster.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second, timeout.Token).ConfigureAwait(true);
        FileLeaseResult additional = await first.Gate!.AcquireAsync(Guid.CreateVersion7(), [Access(first, "/another")], EFileConflictPolicy.Fail, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, additional.Outcome);
        FileReservationLedger upgraded = await FileReservationLedger.ReadAsync(firstStore, "file:reservations", timeout.Token).ConfigureAwait(true);
        Assert.True(upgraded.Slotted);
        Assert.Equal(2, upgraded.Active.Count);
        Assert.Contains(original!.Id, upgraded.Active.Keys);
        await first.Cluster.StopAsync(timeout.Token).ConfigureAwait(true);
        SlotPeer oldFirst = new(firstStore, id, "first", port, 10, false, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable oldFirstLifetime = oldFirst.ConfigureAwait(true);
        await oldFirst.Cluster.StartAsync().ConfigureAwait(true);
        await ReadyAsync(oldFirst, second, timeout.Token).ConfigureAwait(true);
        string expectedCoordinator = "first";
        while (!string.Equals(second.Cluster.CoordinatorId, expectedCoordinator, StringComparison.Ordinal))
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        Assert.False(await second.Gate!.RecoverEndedAsync(false, timeout.Token).ConfigureAwait(true));
        FileReservationLedger restored = await FileReservationLedger.ReadAsync(secondStore, "file:reservations", timeout.Token).ConfigureAwait(true);
        Assert.False(restored.Slotted);
        Assert.Equal(2, restored.Active.Count);
        Dictionary<Guid, FileLease> legacy = (await firstStore.ReadAsync("file:reservations", "active", timeout.Token).ConfigureAwait(true))!.Value!.Json.Deserialize<Dictionary<Guid, FileLease>>()!;
        Assert.Equal(2, legacy.Count);
        Assert.Equal(original.ReservationId, legacy[original.Id].ReservationId);
        Assert.Equal(additional.Lease!.ReservationId, legacy[additional.Lease.Id].ReservationId);
        Assert.All(await secondStore.ListAsync("file:reservations:slots", timeout.Token).ConfigureAwait(true), entry => Assert.Null(entry.Value));
    }

    /// <summary>通常停止では保持予約ごと旧形式へ戻し、その後の受付で新形式へ戻らないことを確認します。</summary>
    /// <returns>単独Coreの更新失敗時も旧版が元の予約を読めることの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopPreparationPreservesHeldReservationsForLegacyReadersAsync(bool detached)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        SlotPeer peer = new(repository, Guid.NewGuid().ToString("N"), "one", LargeSynchronizationTests.Port(), 1, true, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        peer.Cluster.LocalDetachedExecutionAllowed = _ => true;
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        if (detached)
        {
            await peer.Cluster.DetachAsync().ConfigureAwait(true);
        }
        FileLeaseResult held = await peer.Gate!.AcquireAsync(Guid.CreateVersion7(), [Access(peer, "/held")], EFileConflictPolicy.Fail).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, held.Outcome);
        string scope = FileReservationLedger.Scope("one", detached);
        Assert.True((await FileReservationLedger.ReadAsync(repository, scope).ConfigureAwait(true)).Slotted);
        await peer.Gate.PrepareStopAsync().ConfigureAwait(true);
        Dictionary<Guid, FileLease> legacy = (await repository.ReadAsync(scope, "active").ConfigureAwait(true))!.Value!.Json.Deserialize<Dictionary<Guid, FileLease>>()!;
        Assert.Equal(held.Lease!.Id, Assert.Single(legacy).Value.Id);
        Assert.Equal(EOutcome.NotExecuted, (await peer.Gate.AcquireAsync(Guid.CreateVersion7(), [Access(peer, "/other")], EFileConflictPolicy.Fail).ConfigureAwait(true)).Outcome);
        Assert.False((await FileReservationLedger.ReadAsync(repository, scope).ConfigureAwait(true)).Slotted);
    }

    /// <summary>通常参加と同じ起動実体の相互観測まで待ちます。</summary>
    /// <returns>形式判定の前提が揃った状態です。</returns>
    private static async Task ReadyAsync(SlotPeer first, SlotPeer second, CancellationToken cancellationToken = default)
    {
        while (first.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2
            || second.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2
            || !first.Cluster.GetPeers().Any(peer => peer.InstanceId == second.Cluster.LocalInstanceId)
            || !second.Cluster.GetPeers().Any(peer => peer.InstanceId == first.Cluster.LocalInstanceId))
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>正規化済みの試験ファイル範囲です。</summary>
    /// <returns>同じHost上で比較する範囲です。</returns>
    private static FileFootprint Access(SlotPeer peer, string path) => new(peer.Cluster.LocalPeerId, peer.Cluster.LocalInstanceId, "host", path, true);

    /// <summary>旧版相当の能力未登録と新しい予約受付を切り替える実通信Peerです。</summary>
    private sealed class SlotPeer : IAsyncDisposable
    {
        /// <summary>通常の保存複製と、新台帳への対応だけを独立に構成します。</summary>
        internal SlotPeer(SqliteRepository repository, string id, string peer, int port, int priority, bool fresh, bool supported)
        {
            Cluster = new(new(id, peer, "host", "127.0.0.1", "127.255.255.255", port, priority, fresh, Path.GetTempPath())
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTimeout = TimeSpan.FromSeconds(2),
            }, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
            if (supported)
            {
                Gate = new(Cluster, repository);
            }
        }

        /// <summary>実通信する起動実体です。</summary>
        internal PeerCluster Cluster
        {
            get;
        }

        /// <summary>新しい予約形式の受付、旧版相当の場合はnullです。</summary>
        internal ClusterFileGate? Gate
        {
            get;
        }

        /// <summary>通信と受付の終了後に調停資源を解放します。</summary>
        /// <returns>この起動実体の終了です。</returns>
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Cluster).DisposeAsync().ConfigureAwait(false);
            Gate?.Dispose();
        }
    }
}
