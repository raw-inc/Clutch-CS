using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>版一致だけで未転送historyを見逃さず、完全同期の確認後に警告を解除します。</summary>
public sealed class SynchronizationRecoveryTests
{
    /// <summary>新しい相手とは現在値が一致していても、過去の操作結果を一度照合します。</summary>
    /// <returns>値を再適用せず、同じ操作の確定結果だけを補完したことです。</returns>
    [Fact]
    public async Task NewPeerWithMatchingCurrentStateRepairsLegacyReceiptAsync()
    {
        Guid operation = Guid.NewGuid();
        DurableEntry current = new("workflow:recovery", "value", operation.ToString("N"), NodeValue.FromObject(42));
        DurableReceipt success = new(operation, "original-request", EOutcome.Succeeded, [current]);
        SyncRecoveryPeer peer = new(new([current], [success]));
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync();
        Assert.True(await peer.Repository.ImportAsync(new([current], [success with { Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.VersionConflict" }])));
        string version = await peer.Repository.GetSharedVersionAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!string.Equals(peer.AnnouncedVersion, version, StringComparison.Ordinal))
        {
            await Task.Delay(10, timeout.Token);
        }
        while ((await peer.Repository.ReadReceiptAsync(operation, timeout.Token))?.Outcome != EOutcome.Succeeded || peer.Completed == 0)
        {
            await peer.AnnounceAsync(version, cancellationToken: timeout.Token);
            await Task.Delay(40, timeout.Token);
        }
        Assert.Equal(version, await peer.Repository.GetSharedVersionAsync());
        DurableEntry retained = Assert.IsType<DurableEntry>(await peer.Repository.ReadAsync(current.Scope, current.Key));
        Assert.Equal(current.Version, retained.Version);
        Assert.Equal(42, retained.Value!.Json.GetInt32());
        Assert.Equal(1, peer.Opens);
    }

    /// <summary>旧版の相手でも、一度の大量送信で末尾が欠ける通信路を通して完全同期できます。</summary>
    /// <returns>小分けに取得した共有値と履歴の取込確認です。</returns>
    [Fact]
    public async Task LegacySenderSynchronizesWhenLargeReplyBurstsLoseTheirTailAsync()
    {
        RepositorySnapshot snapshot = Snapshot();
        SyncRecoveryPeer peer = new(snapshot) { MaximumReplyFragments = 16 };
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (peer.Target.SynchronizationProblem is null && await peer.Repository.ReadReceiptAsync(snapshot.Receipts[0].OperationId, timeout.Token).ConfigureAwait(true) is null)
        {
            await peer.AnnounceAsync("different", cancellationToken: timeout.Token).ConfigureAwait(true);
            await Task.Delay(40, timeout.Token).ConfigureAwait(true);
        }
        Assert.Null(peer.Target.SynchronizationProblem);
        DurableEntry current = Assert.IsType<DurableEntry>(await peer.Repository.ReadAsync("workflow:recovery", "value", timeout.Token).ConfigureAwait(true));
        Assert.Equal(snapshot.Entries[0].Value!.Json.GetString(), current.Value!.Json.GetString());
        Assert.NotNull(await peer.Repository.ReadReceiptAsync(snapshot.Receipts[0].OperationId, timeout.Token).ConfigureAwait(true));
        Assert.Equal(1, peer.Opens);
    }

    /// <summary>現在値が一致しても失敗相手との完全同期を再試行し、欠けていた履歴を取り込みます。</summary>
    /// <returns>実UDP・SQLiteの収束確認です。</returns>
    [Fact]
    public async Task MatchingDigestRetriesFailedTransferAndRecoversMissingHistory()
    {
        RepositorySnapshot snapshot = Snapshot();
        RequestTestClock clock = new();
        clock.Freeze();
        SyncRecoveryPeer peer = new(snapshot, clock: clock) { DropReads = true };
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        Assert.True(await peer.Repository.ImportAsync(snapshot with { Receipts = [] }).ConfigureAwait(true));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await FailAsync(peer, timeout.Token).ConfigureAwait(true);
        string digest = await peer.Repository.GetSharedVersionAsync(timeout.Token).ConfigureAwait(true);
        Assert.Null(await peer.Repository.ReadReceiptAsync(snapshot.Receipts[0].OperationId, timeout.Token).ConfigureAwait(true));
        int attempts = peer.Opens;
        for (int index = 0; index < 4; index++)
        {
            await peer.AnnounceAsync(digest, cancellationToken: timeout.Token).ConfigureAwait(true);
            await Task.Delay(40, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(attempts, peer.Opens);
        Assert.Equal("Sync.ChunkUnconfirmed", peer.Target.SynchronizationProblem);
        peer.DropReads = false;

        // 失敗後の最初の再試行間隔だけ進めます。UDPの配送期限と観測待ちは実時間のままです。
        clock.AdvanceAndResume(TimeSpan.FromSeconds(1));
        while (peer.Target.SynchronizationProblem is not null)
        {
            await peer.AnnounceAsync(digest, cancellationToken: timeout.Token).ConfigureAwait(true);
            await Task.Delay(40, timeout.Token).ConfigureAwait(true);
        }
        Assert.True(attempts < peer.Opens);
        Assert.NotNull(await peer.Repository.ReadReceiptAsync(snapshot.Receipts[0].OperationId, timeout.Token).ConfigureAwait(true));
        Assert.Equal(digest, await peer.Repository.GetSharedVersionAsync(timeout.Token).ConfigureAwait(true));
        Assert.Equal(EPeerStatus.Ready, peer.Target.GetPeers().Single(item => item.InstanceId == peer.Target.LocalInstanceId).Status);
    }

    /// <summary>一回の配送期限を超えても固定断片だけ再取得し、スナップショット全体を再開始しません。</summary>
    /// <returns>元セッションでの完了です。</returns>
    [Fact]
    public async Task LostChunkRequestRetriesTheSameSnapshotWithoutAnotherExport()
    {
        RepositorySnapshot snapshot = Snapshot();
        SyncRecoveryPeer peer = new(snapshot) { DropFirstReadRequest = true };
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (await peer.Repository.ReadReceiptAsync(snapshot.Receipts[0].OperationId, timeout.Token).ConfigureAwait(true) is null)
        {
            await peer.AnnounceAsync("different", cancellationToken: timeout.Token).ConfigureAwait(true);
            await Task.Delay(40, timeout.Token).ConfigureAwait(true);
        }
        await peer.AnnounceAsync(await peer.Repository.GetSharedVersionAsync(timeout.Token).ConfigureAwait(true), EPeerStatus.Syncing, timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, peer.Opens);
        Assert.Null(peer.Target.SynchronizationProblem);
    }

    /// <summary>旧closeと不一致の完了主張は解除根拠にせず、検証した双方向完了だけを採用します。</summary>
    /// <returns>相手開始の同期での警告状態です。</returns>
    [Theory]
    [InlineData("completed", true)]
    [InlineData("legacy", false)]
    [InlineData("wrong-export", false)]
    public async Task IncomingSynchronizationRequiresMatchingCompletionBeforeClearingFailure(string mode, bool cleared)
    {
        SyncRecoveryPeer peer = new(Snapshot()) { DropReads = true };
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await FailAsync(peer, timeout.Token).ConfigureAwait(true);
        await peer.AnnounceAsync("different", EPeerStatus.Syncing, timeout.Token).ConfigureAwait(true);
        string sourceId = "source";
        while (peer.Target.GetPeers().Single(item => string.Equals(item.PeerId, sourceId, StringComparison.Ordinal)).Status != EPeerStatus.Syncing)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        peer.DropReads = false;
        SyncManifest offered = peer.Offer();
        SyncResponse result = await peer.SendAsync("sync.open", offered, timeout.Token).ConfigureAwait(true);
        while (result.Pending)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            result = await peer.SendAsync("sync.result", offered.Id, timeout.Token).ConfigureAwait(true);
        }
        Assert.Null(result.Reason);
        SyncManifest exported = Assert.IsType<SyncManifest>(result.Manifest);
        Assert.Equal("Sync.ChunkUnconfirmed", peer.Target.SynchronizationProblem);
        using MemoryStream received = new();
        for (long offset = 0; offset < exported.Length;)
        {
            int count = checked((int)Math.Min(65536, exported.Length - offset));
            SyncResponse read = await peer.SendAsync("sync.read", new SyncReadRequest(exported.Id, offset, count), timeout.Token).ConfigureAwait(true);
            SyncChunk chunk = Assert.IsType<SyncChunk>(read.Chunk);
            Assert.Equal(offset, chunk.Offset);
            Assert.Equal(count, chunk.Data.Length);
            received.Write(chunk.Data);
            offset += count;
        }
        Assert.Equal(exported.Hash, Convert.ToHexString(SHA256.HashData(received.ToArray())));
        Guid exportId = exported.Id;
        string wrongExport = "wrong-export";
        string legacy = "legacy";
        if (string.Equals(mode, wrongExport, StringComparison.Ordinal))
        {
            exportId = Guid.NewGuid();
        }
        if (string.Equals(mode, legacy, StringComparison.Ordinal))
        {
            _ = await peer.SendAsync("sync.close", new { ExportId = exportId, ImportId = offered.Id }, timeout.Token).ConfigureAwait(true);
        }
        else
        {
            _ = await peer.SendAsync("sync.close", new SyncCloseRequest(exportId, offered.Id, true), timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(cleared, peer.Target.SynchronizationProblem is null);
    }

    /// <summary>既存の通常参加Peerに断片未確認を発生させます。</summary>
    /// <returns>警告を観測した時点です。</returns>
    private static async Task FailAsync(SyncRecoveryPeer peer, CancellationToken cancellationToken = default)
    {
        while (peer.Target.SynchronizationProblem is null)
        {
            await peer.AnnounceAsync("different", cancellationToken: cancellationToken).ConfigureAwait(true);
            await Task.Delay(40, cancellationToken).ConfigureAwait(true);
        }
        Assert.Equal("Sync.ChunkUnconfirmed", peer.Target.SynchronizationProblem);
    }

    /// <summary>現在値に加えて版比較では分からない履歴を含めます。</summary>
    /// <returns>複数断片で転送する共有状態です。</returns>
    private static RepositorySnapshot Snapshot()
    {
        DurableEntry current = new("workflow:recovery", "value", "current", NodeValue.FromObject(new string('x', 90000)), "previous");
        DurableEntry previous = new("workflow:recovery", "value", "previous", NodeValue.FromObject(1));
        return new([current], [new(Guid.NewGuid(), "history", EOutcome.Succeeded, [previous])]);
    }
}
