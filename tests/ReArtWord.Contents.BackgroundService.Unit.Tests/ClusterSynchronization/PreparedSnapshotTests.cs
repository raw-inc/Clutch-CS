using System.Net;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>送信側の照合前全書出しを防ぎ、固定版・容量・読取り排他・予約枠を確認します。</summary>
[Collection("SnapshotCost")]
public sealed class PreparedSnapshotTests(ITestOutputHelper output)
{
    /// <summary>同じ32MiBの履歴で従来の全書出し後の差分化と比較し、本文を作らない効果を測ります。</summary>
    /// <returns>同じ転送本文、実際の作業ファイル長、時間と累積割当の比較です。</returns>
    [Fact]
    public async Task InventoryFirstAvoidsHistoricalWriteAmplificationAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryAsync(files.Root, true).ConfigureAwait(true);
        RepositorySnapshot original = await SeedAsync(repository, 512).ConfigureAwait(true);
        string[] known = SyncInventory.Describe(original);
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        SyncExportStore exports = Create(peer, repository, capacity: 134217728);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        using (SyncPreparedExport? warm = await exports.PrepareAsync(local).ConfigureAwait(true))
        {
            Assert.NotNull(warm);
        }
        long before = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        SyncManifest legacy = await exports.CreateAsync(local, true).ConfigureAwait(true);
        long legacyWritten = legacy.Length;
        legacy = (await exports.NarrowAsync(legacy, known).ConfigureAwait(true)).Manifest;
        long legacyBytes = GC.GetTotalAllocatedBytes(true) - before;
        double legacyMilliseconds = watch.Elapsed.TotalMilliseconds;
        legacyWritten += legacy.Length;
        await exports.ReleaseAsync(legacy.Id).ConfigureAwait(true);
        before = GC.GetTotalAllocatedBytes(true);
        watch.Restart();
        using SyncPreparedExport prepared = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true));
        SyncManifest current = await exports.MaterializeAsync(prepared, known).ConfigureAwait(true);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        double milliseconds = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(legacy.Hash, current.Hash);
        Assert.True(32L * 1024 * 1024 < legacyWritten);
        Assert.InRange(current.Length, 1, 8192);
        Assert.True(allocated * 4 < legacyBytes, JsonSerializer.Serialize(new { allocated, legacyBytes }));
        output.WriteLine(JsonSerializer.Serialize(new { historyBytes = 32L * 1024 * 1024, legacyMilliseconds, legacyBytes, legacyWritten,
            milliseconds, allocatedBytes = allocated, writtenBytes = current.Length }));
        await exports.ReleaseAsync(current.Id).ConfigureAwait(true);
    }

    /// <summary>履歴全体が転送上限より大きくても、一致する本文を復元・書出しせず照合できます。</summary>
    /// <returns>SQLite単体と用途振分け双方の、空差分と割当上限です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingInventoryAvoidsFullHistoryMaterializationAsync(bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryAsync(files.Root, routed).ConfigureAwait(true);
        RepositorySnapshot original = await SeedAsync(repository).ConfigureAwait(true);
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        SyncExportStore exports = Create(peer, repository, 8192);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        using (SyncPreparedExport? warm = await exports.PrepareAsync(local).ConfigureAwait(true))
        {
            Assert.NotNull(warm);
        }
        long before = GC.GetTotalAllocatedBytes(true);
        using SyncPreparedExport prepared = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true));
        string[] hashes = prepared.Request.Hashes.ToArray();
        SyncManifest manifest = await exports.MaterializeAsync(prepared, hashes).ConfigureAwait(true);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.InRange(allocated, 0, 8L * 1024 * 1024);
        Assert.Equal(prepared.Original.Id, manifest.Id);
        Assert.Equal(prepared.Original.ExpiresAt, manifest.ExpiresAt);
        Assert.InRange(manifest.Length, 1, 8192);
        RepositorySnapshot actual = await ReadAsync(exports, manifest, local).ConfigureAwait(true);
        string[] originalHashes = SyncInventory.Describe(original);
        Assert.Equal(JsonSerializer.Serialize(SyncInventory.Difference(original, originalHashes, originalHashes)), JsonSerializer.Serialize(actual));
        await exports.ReleaseAsync(manifest.Id).ConfigureAwait(true);
    }

    /// <summary>照合中の通常書込みを妨げず、後から書き出す本文は準備時の版を維持します。</summary>
    /// <returns>後着変更の除外、元の全本文、同じ準備の二重使用拒否です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesContinueWhilePreparedSnapshotKeepsItsOriginalContentsAsync(bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryAsync(files.Root, routed).ConfigureAwait(true);
        RepositorySnapshot original = await SeedAsync(repository).ConfigureAwait(true);
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        SyncExportStore exports = Create(peer, repository);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        using SyncPreparedExport prepared = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true));
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(5));
        DurableEntry current = Assert.Single(original.Entries);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new(current.Scope, current.Key, current.Version, NodeValue.FromObject("later"))], limit.Token).ConfigureAwait(true)).Outcome);
        SyncManifest manifest = await exports.MaterializeAsync(prepared, null, limit.Token).ConfigureAwait(true);
        RepositorySnapshot actual = await ReadAsync(exports, manifest, local, limit.Token).ConfigureAwait(true);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(actual));
        _ = await Assert.ThrowsAsync<SyncFailureException>(() => exports.MaterializeAsync(prepared, null, limit.Token)).ConfigureAwait(true);
        await exports.ReleaseAsync(manifest.Id, limit.Token).ConfigureAwait(true);
    }

    /// <summary>照合後の圧縮・旧形式移行を許容し、同じ固定本文を再試行せず配送します。</summary>
    /// <returns>直接・用途振分け双方で、同期準備と保守が交錯しても元内容を維持する確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ArchivedReceiptStillMaterializesThePreparedSnapshotAsync(bool routed, bool legacy)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryAsync(files.Root, routed).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HistoryArchiveService archive = new(repository);
        await archive.MaintainAsync(new(), new(), now).ConfigureAwait(true);
        string scope = "workflow:fixed", key = "value";
        if (legacy)
        {
            scope = "file:reservations";
            key = "active";
        }
        DurableReceipt old = await repository.ApplyBatchAsync(Guid.CreateVersion7(now),
            [new(scope, key, null, NodeValue.FromObject(new Dictionary<Guid, object>()))]).ConfigureAwait(true);
        await repository.ApplyBatchAsync(Guid.CreateVersion7(now),
            [new(scope, key, old.Entries[0].Version, NodeValue.FromObject(new { Format = 2 }))]).ConfigureAwait(true);
        RepositorySnapshot original = await repository.ExportAsync().ConfigureAwait(true);
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        SyncExportStore exports = Create(peer, repository);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        using SyncPreparedExport prepared = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true));
        DataRetentionSettings retention = new(RawDays: 0, TotalDays: 30);
        DateTimeOffset maintenanceAt = now.AddDays(2);
        if (legacy)
        {
            retention = retention with { RawDays = 7 };
            maintenanceAt = now.AddMinutes(11);
        }
        for (int pass = 0; pass < 8; pass++)
        {
            await archive.MaintainAsync(new(), retention, maintenanceAt).ConfigureAwait(true);
        }
        Assert.DoesNotContain((await repository.ExportAsync().ConfigureAwait(true)).Receipts, receipt => receipt.OperationId == old.OperationId);
        SyncManifest manifest = await exports.MaterializeAsync(prepared, null).ConfigureAwait(true);
        RepositorySnapshot actual = await ReadAsync(exports, manifest, local).ConfigureAwait(true);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(actual));
        await exports.ReleaseAsync(manifest.Id).ConfigureAwait(true);
    }

    /// <summary>固定後に消えた履歴や同じIDの異内容を、成功した元スナップショットとして送りません。</summary>
    /// <returns>内容変更の拒否と、失敗後に予約枠が戻ることの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedReceiptDoesNotPublishAMixedSnapshotAsync(bool remove)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryAsync(files.Root, false).ConfigureAwait(true);
        RepositorySnapshot original = await SeedAsync(repository).ConfigureAwait(true);
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        SyncExportStore exports = Create(peer, repository, sessions: 1);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        using (SyncPreparedExport prepared = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true)))
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(files.Root, "state.db"), Pooling = false }.ToString());
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET payload=$payload,hash=$hash WHERE kind='receipt' AND key=$key";
            if (remove)
            {
                command.CommandText = "DELETE FROM records WHERE kind='receipt' AND key=$key";
            }
            command.Parameters.AddWithValue("$key", original.Receipts[0].OperationId.ToString("N"));
            string payload = JsonSerializer.Serialize(original.Receipts[0] with { Fingerprint = "changed" });
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
            Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
            _ = await Assert.ThrowsAsync<SnapshotChangedException>(() => exports.MaterializeAsync(prepared, null)).ConfigureAwait(true);
        }
        using SyncPreparedExport next = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true));
    }

    /// <summary>取消と期限切れでファイルを公開せず、同じ最大一枠を次の同期へ返します。</summary>
    /// <returns>失敗後の再予約と空差分の成功です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndExpiryReturnThePreparationSlotAsync(bool expire)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryAsync(files.Root, false).ConfigureAwait(true);
        await SeedAsync(repository, 1).ConfigureAwait(true);
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        SyncExportStore exports = Create(peer, repository, sessions: 1, lifetime: TimeSpan.FromMilliseconds(100));
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        using (SyncPreparedExport prepared = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true)))
        {
            if (expire)
            {
                await Task.Delay(200).ConfigureAwait(true);
                SyncFailureException failure = await Assert.ThrowsAsync<SyncFailureException>(() => exports.MaterializeAsync(prepared, null)).ConfigureAwait(true);
                Assert.Equal("Sync.SessionExpired", failure.Code);
            }
            else
            {
                using CancellationTokenSource cancelled = new();
                await cancelled.CancelAsync().ConfigureAwait(true);
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exports.MaterializeAsync(prepared, null, cancelled.Token)).ConfigureAwait(true);
            }
        }
        using SyncPreparedExport next = Assert.IsType<SyncPreparedExport>(await exports.PrepareAsync(local).ConfigureAwait(true));
        SyncManifest manifest = await exports.MaterializeAsync(next, next.Request.Hashes).ConfigureAwait(true);
        await exports.ReleaseAsync(manifest.Id).ConfigureAwait(true);
    }

    /// <summary>現在値は一つのまま、異なる過去版の本文を4MiB積み上げます。</summary>
    /// <returns>元の全内容です。準備や転送の計測範囲には含めません。</returns>
    private static async Task<RepositorySnapshot> SeedAsync(IDurableRepository repository, int count = 64, CancellationToken cancellationToken = default)
    {
        string? version = null;
        for (int index = 0; index < count; index++)
        {
            NodeValue value = NodeValue.FromObject(index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 65528));
            DurableMutation[] changes = [new("workflow:fixed", "value", version, value)];
            version = Assert.Single((await repository.ApplyBatchAsync(Guid.NewGuid(), changes, cancellationToken).ConfigureAwait(false)).Entries).Version;
        }
        return await repository.ExportAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通常の用途振分けと直接SQLiteの双方で同じ固定参照を確認します。</summary>
    /// <returns>この試験だけの保存先です。</returns>
    private static async Task<IDurableRepository> RepositoryAsync(string root, bool routed, CancellationToken cancellationToken = default)
    {
        if (routed)
        {
            return await RepositoryFactory.CreateAsync(root, "fixture", new(), cancellationToken).ConfigureAwait(false);
        }
        return new SqliteRepository(Path.Combine(root, "state.db"));
    }

    /// <summary>試験専用Peerと実ループバック上で固定書出しを提供します。</summary>
    /// <returns>呼出元が非同期破棄する所有Storeです。</returns>
    private static SyncExportStore Create(SyncRecoveryPeer peer, IDurableRepository repository, long capacity = 16777216, int sessions = 4, TimeSpan? lifetime = null)
    {
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".")
        {
            MaximumSyncSnapshotBytes = capacity, MaximumSyncSessions = sessions, SyncSessionLifetime = lifetime ?? TimeSpan.FromSeconds(15),
        };
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        return new(peer.Target, repository, options, TimeProvider.System, () => new(network, local.Address));
    }

    /// <summary>受信可能な固定マニフェストの全バイトを読み取り、実際のJSONを確認します。</summary>
    /// <returns>同じ固定版から配送された内容です。</returns>
    private static async Task<RepositorySnapshot> ReadAsync(SyncExportStore exports, SyncManifest manifest, PeerInfo peer, CancellationToken cancellationToken = default)
    {
        using MemoryStream bytes = new();
        while (bytes.Length < manifest.Length)
        {
            SyncChunk chunk = await exports.ReadAsync(new(manifest.Id, bytes.Length, 65536), peer.PeerId, peer.InstanceId, cancellationToken).ConfigureAwait(false);
            bytes.Write(chunk.Data);
        }
        return JsonSerializer.Deserialize<RepositorySnapshot>(bytes.ToArray())!;
    }
}
