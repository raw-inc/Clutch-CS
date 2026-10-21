using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>クライアント再起動とPeer再起動を越える固定要求の保存境界を確認します。</summary>
public sealed class CommandLedgerTests
{
    /// <summary>秘密本文を保存せず、再起動後の同要求を新しいPeer実体へ付け替えません。</summary>
    /// <returns>元要求の同一性の確認です。</returns>
    [Fact]
    public async Task RestartPreservesOriginalInstanceAndReceiptFingerprintWithoutSecretBody()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("contents-client-ledger-");
        try
        {
            string secret = "ledger-must-not-persist-this-secret";
            NodeValue payload = NodeValue.FromObject(new { name = "test-key", value = secret });
            Guid id = Guid.NewGuid();
            PeerInfo peer = Peer();
            ManagementCommand first;
            using (ClientCommandLedger ledger = new(directory.FullName))
            {
                first = await ledger.PrepareAsync(id, "cluster", peer.PeerId, "secrets.set", payload, peer).ConfigureAwait(true);
            }
            using ClientCommandLedger restarted = new(directory.FullName);
            ManagementCommand same = await restarted.PrepareAsync(id, "cluster", peer.PeerId, "secrets.set", payload, peer with { InstanceId = Guid.NewGuid() }).ConfigureAwait(true);
            Assert.Equal(first.TargetInstanceId, same.TargetInstanceId);
            Assert.Equal(first.ExpiresAt, same.ExpiresAt);
            ClientCommandRecord record = Assert.IsType<ClientCommandRecord>(await restarted.ReadAsync(id).ConfigureAwait(true));
            Assert.Equal(ContractJson.Hash(ManagementWire.Request(first, false)), record.RequestFingerprint);
            foreach (string path in Directory.EnumerateFiles(directory.FullName, "*.json", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(secret, await File.ReadAllTextAsync(path).ConfigureAwait(true), StringComparison.Ordinal);
                Assert.DoesNotContain("test-key", await File.ReadAllTextAsync(path).ConfigureAwait(true), StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>同じIDで要求内容や接続先を変更した再送を拒否します。</summary>
    /// <returns>新しい副作用を作らない確認です。</returns>
    [Fact]
    public async Task SameRequestCannotChangeBodyOrCluster()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("contents-client-ledger-");
        try
        {
            using ClientCommandLedger ledger = new(directory.FullName);
            Guid id = Guid.NewGuid();
            PeerInfo peer = Peer();
            NodeValue payload = NodeValue.FromObject(42);
            _ = await ledger.PrepareAsync(id, "cluster", peer.PeerId, "node", payload, peer).ConfigureAwait(true);
            await Assert.ThrowsAsync<ArgumentException>(() => ledger.PrepareAsync(id, "cluster", peer.PeerId, "node", NodeValue.FromObject(43), peer)).ConfigureAwait(true);
            await Assert.ThrowsAsync<ArgumentException>(() => ledger.PrepareAsync(id, "another-cluster", peer.PeerId, "node", payload, peer)).ConfigureAwait(true);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>未発見のPeerへ初回要求の実体を捏造しません。</summary>
    /// <returns>送信前記録も作らない確認です。</returns>
    [Fact]
    public async Task UndiscoveredPeerCannotStartNewRequest()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("contents-client-ledger-");
        try
        {
            using ClientCommandLedger ledger = new(directory.FullName);
            Guid id = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.PrepareAsync(id, "cluster", "missing", "node", null, null)).ConfigureAwait(true);
            Assert.Null(await ledger.ReadAsync(id).ConfigureAwait(true));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>発見済みとする元のPeer実体を作成します。</summary>
    /// <returns>固定先の観測情報です。</returns>
    private static PeerInfo Peer() => new("peer", Guid.NewGuid(), "host", "127.0.0.1", 48000, EPeerStatus.Ready, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
