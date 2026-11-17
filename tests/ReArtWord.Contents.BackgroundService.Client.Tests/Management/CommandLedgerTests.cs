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
    /// <summary>ClientHostを再起動しても、初回に換算した期限で再送されることを確認します。</summary>
    /// <returns>期限・対象・ハッシュの保持と、操作本文を保存しないことを検証するタスクです。</returns>
    [Fact]
    public async Task RestartRestoresTheOriginalDeliveryClockWithoutRecalibration()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("clutch-command-clock-");
        try
        {
            PeerInfo peer = Peer();
            Guid id = Guid.NewGuid();
            string secret = "command-clock-secret-must-stay-in-memory";
            NodeValue body = NodeValue.FromObject(secret);
            ManagementCommand first;
            using (ClientCommandLedger ledger = new(directory.FullName))
            {
                first = await ledger.PrepareTimedAsync(id, "cluster", peer.PeerId, "secrets.set", body, peer,
                    (command, _) => Task.FromResult<Contracts.Execution.RequestClockStamp?>(new(peer.InstanceId, 12345, command.ExpiresAt.AddMinutes(-5)))).ConfigureAwait(true);
            }
            using ClientCommandLedger restarted = new(directory.FullName);
            ManagementCommand restored = await restarted.PrepareTimedAsync(id, "cluster", peer.PeerId, "secrets.set", body, peer with { InstanceId = Guid.NewGuid() },
                (_, _) => throw new InvalidOperationException("A retry must not request another clock.")).ConfigureAwait(true);
            Assert.True(restored.DeliveryPrepared);
            Assert.Equal(first.DeliveryTiming, restored.DeliveryTiming);
            Assert.Equal(first.TargetInstanceId, restored.TargetInstanceId);
            Assert.Equal(first.ExpiresAt, restored.ExpiresAt);
            Assert.Equal(ContractJson.Hash(ManagementWire.Request(first, false)), ContractJson.Hash(ManagementWire.Request(restored, false)));
            foreach (string path in Directory.EnumerateFiles(directory.FullName, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(secret, await File.ReadAllTextAsync(path).ConfigureAwait(true), StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>時計照会の失敗後も要求が残り、同じClientHostプロセスなら残り時間内で準備を再開できることを確認します。</summary>
    /// <returns>準備の再開と、再起動後の送信拒否を検証するタスクです。</returns>
    [Fact]
    public async Task InterruptedPreparationCannotIssueANewDeadlineAfterRestart()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("clutch-command-preparation-");
        try
        {
            PeerInfo peer = Peer();
            Guid firstId = Guid.NewGuid(), interruptedId = Guid.NewGuid();
            using (ClientCommandLedger ledger = new(directory.FullName))
            {
                await Assert.ThrowsAsync<IOException>(() => ledger.PrepareTimedAsync(firstId, "cluster", peer.PeerId, "write", null, peer,
                    (_, _) => throw new IOException("Clock response unavailable."))).ConfigureAwait(true);
                ClientCommandRecord original = (await ledger.ReadAsync(firstId).ConfigureAwait(true))!;
                ManagementCommand ready = await ledger.PrepareTimedAsync(firstId, "cluster", peer.PeerId, "write", null, peer, (command, _) =>
                {
                    Assert.NotNull(command.Admission);
                    Assert.InRange(command.Admission.Remaining, TimeSpan.Zero, TimeSpan.FromMinutes(5));
                    return Task.FromResult<Contracts.Execution.RequestClockStamp?>(new(peer.InstanceId, 45678, original.ExpiresAt.AddMinutes(-5)));
                }).ConfigureAwait(true);
                Assert.Equal(original.ExpiresAt, ready.ExpiresAt);
                await Assert.ThrowsAsync<IOException>(() => ledger.PrepareTimedAsync(interruptedId, "cluster", peer.PeerId, "write", null, peer,
                    (_, _) => throw new IOException("Clock response unavailable."))).ConfigureAwait(true);
            }
            using ClientCommandLedger restarted = new(directory.FullName);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareTimedAsync(interruptedId, "cluster", peer.PeerId, "write", null, peer,
                (_, _) => throw new IOException("Must not query another clock after interrupted preparation."))).ConfigureAwait(true);
            Assert.NotNull(await restarted.ReadAsync(interruptedId).ConfigureAwait(true));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>旧版で保存した要求が、同じ期限・同じ通信形式で再送されることを確認します。</summary>
    /// <returns>旧版の要求記録との互換性を検証するタスクです。</returns>
    [Fact]
    public async Task ExistingRecordsDoNotAcquireNewClockMetadata()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("clutch-command-legacy-");
        try
        {
            PeerInfo peer = Peer();
            Guid id = Guid.NewGuid();
            using ClientCommandLedger ledger = new(directory.FullName);
            ManagementCommand original = await ledger.PrepareAsync(id, "cluster", peer.PeerId, "write", null, peer).ConfigureAwait(true);
            ManagementCommand same = await ledger.PrepareTimedAsync(id, "cluster", peer.PeerId, "write", null, peer,
                (_, _) => throw new IOException("A legacy retry must keep its delivery format.")).ConfigureAwait(true);
            Assert.True(same.DeliveryPrepared);
            Assert.Null(same.DeliveryTiming);
            Assert.Equal(original.ExpiresAt, same.ExpiresAt);
        }
        finally
        {
            directory.Delete(true);
        }
    }

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
