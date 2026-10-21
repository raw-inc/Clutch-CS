using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>再読取りを省いても、固定した同期本文と保持境界を変えません。</summary>
public sealed class SyncExportInventoryTests
{
    /// <summary>全区分の一致時にも旧契約の省略状態と非区分の保持情報を転送します。</summary>
    /// <param name="legacy">旧形式の省略された配列も維持します。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataMatchesTheCompleteDifferenceWithoutKeepingMutableSources(bool legacy)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Dictionary<ERetentionCategory, DateTimeOffset> boundary = new() { [ERetentionCategory.SharedHistory] = now.AddDays(-3) };
        string[] protectedHistory = ["retained-version"];
        Guid[] pending = [Guid.NewGuid()];
        DurableEntry entry = new("workflow:inventory", "key", "version", NodeValue.FromObject("payload"));
        RepositorySnapshot snapshot = new([entry], [new(Guid.NewGuid(), "fingerprint", EOutcome.Succeeded, [entry])],
            OperationOwners: [new(Guid.NewGuid(), "owner", [entry.Scope])], CausalEntries: [new(entry.Scope, entry.Key, entry.Version, "{}")],
            LegacyEntries: [new(entry.Scope, entry.Key, "ancestor", "value-hash", [])],
            HistoryBoundary: new(boundary, new Dictionary<ERetentionCategory, DateTimeOffset>(), now.AddDays(-30), now),
            RetiredOperations: [new(Guid.NewGuid(), "retired", true)], ProtectedHistory: protectedHistory,
            RetiredKeys: [new("request-history", "retired-key")], PendingOperations: pending);
        if (legacy)
        {
            snapshot = new(snapshot.Entries, snapshot.Receipts);
        }
        string[] hashes = SyncInventory.Describe(snapshot);
        string expected = JsonSerializer.Serialize(SyncInventory.Difference(snapshot, hashes, hashes));
        RepositorySnapshot metadata = SyncInventory.Metadata(snapshot);
        Assert.Equal(expected, JsonSerializer.Serialize(metadata));
        boundary.Clear();
        protectedHistory[0] = "changed";
        pending[0] = Guid.NewGuid();
        Assert.Equal(expected, JsonSerializer.Serialize(metadata));
    }

    /// <summary>作成後の保存変更や呼出元の配列変更から独立して、元の版だけを配信します。</summary>
    /// <param name="partial">一部の区分だけ不一致とするかです。</param>
    /// <returns>固定版の差分を実TCPで読んだ結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedInventoryKeepsTheOriginalSnapshotAndItsWireContentsAsync(bool partial)
    {
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".");
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        LocalNetworkBinding binding = new(network, local.Address);
        SyncExportStore exports = new(peer.Target, peer.Repository, options, TimeProvider.System, () => binding);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        DurableMutation[] mutations = Enumerable.Range(0, 32).Select(index => new DurableMutation("workflow:inventory", "key-" + index,
            null, NodeValue.FromObject(new string('x', 1024)))).ToArray();
        Assert.Equal(EOutcome.Succeeded, (await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), mutations, timeout.Token).ConfigureAwait(true)).Outcome);
        RepositorySnapshot original = await peer.Repository.ExportAsync(timeout.Token).ConfigureAwait(true);
        string[] originalHashes = SyncInventory.Describe(original);
        SyncManifest manifest = await exports.CreateAsync(local, true, timeout.Token).ConfigureAwait(true);
        string[] hashes = Assert.IsType<string[]>(exports.Describe(manifest).Hashes);
        Assert.Equal(originalHashes, hashes);
        hashes[0] = new string('0', 64);
        Assert.Equal(originalHashes, exports.Describe(manifest).Hashes);
        _ = await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:inventory", "late", null, NodeValue.FromObject("not in this export"))], timeout.Token).ConfigureAwait(true);
        string[] known = originalHashes.ToArray();
        if (partial)
        {
            Array.Fill(known, new string('0', 64), 0, 128);
        }
        RepositorySnapshot expected = SyncInventory.Difference(original, originalHashes, known);
        SyncInventoryOffer offer = await exports.NarrowAsync(manifest, known, timeout.Token).ConfigureAwait(true);
        Assert.Equal(originalHashes, offer.Hashes);
        using MemoryStream received = new();
        Assert.True(await SyncStreamDownload.CopyAsync(offer.Manifest, binding, local.Address, received, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(true));
        received.Position = 0;
        RepositorySnapshot actual = (await JsonSerializer.DeserializeAsync<RepositorySnapshot>(received, cancellationToken: timeout.Token).ConfigureAwait(true))!;
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.DoesNotContain(actual.Entries, entry => string.Equals(entry.Key, "late", StringComparison.Ordinal));
    }

    /// <summary>既知区分がある受信側は、全体をファイルへ書いて読み戻さず同じ差分を返します。</summary>
    /// <returns>一致・部分差分・保持区間不一致での完全転送の同値性です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DirectInventoryWritesOnlyTheRequiredSnapshotAsync(bool partial, bool full)
    {
        SyncRecoveryPeer peer = new(new([], []));
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".");
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        LocalNetworkBinding binding = new(network, local.Address);
        SyncExportStore exports = new(peer.Target, peer.Repository, options, TimeProvider.System, () => binding);
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        for (int index = 0; index < 16; index++)
        {
            _ = await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:direct", "key-" + index, null, NodeValue.FromObject(new string('x', 4096)))], timeout.Token).ConfigureAwait(true);
        }
        RepositorySnapshot original = await peer.Repository.ExportAsync(timeout.Token).ConfigureAwait(true);
        string[] hashes = SyncInventory.Describe(original);
        string[] known = hashes.ToArray();
        if (partial)
        {
            Array.Fill(known, new string('0', 64), 0, 128);
        }
        HistoryRetentionBoundary? boundary = null;
        if (full)
        {
            boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow);
        }
        SyncInventoryRequest request = new(Guid.NewGuid(), known, DateTimeOffset.UtcNow.AddSeconds(20), boundary);
        SyncInventoryOffer offer = await exports.CreateOfferAsync(local, request, timeout.Token).ConfigureAwait(true);
        RepositorySnapshot expected = full ? original
                                           : SyncInventory.Difference(original, hashes, known);
        using MemoryStream received = new();
        Assert.True(await SyncStreamDownload.CopyAsync(offer.Manifest, binding, local.Address, received, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(true));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), received.ToArray());
        Assert.Equal(hashes, offer.Hashes);
        Assert.Equal(full, offer.Full);
        if (!partial && !full)
        {
            Assert.InRange(offer.Manifest.Length, 1, 1024);
        }
    }

}
