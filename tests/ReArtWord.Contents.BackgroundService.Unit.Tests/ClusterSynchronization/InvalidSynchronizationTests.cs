using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>片方向の破損・切断・実体不一致では部分ImportもReady移行もしません。</summary>
public sealed class InvalidSynchronizationTests
{
    /// <summary>逆方向の完成していない共有状態を通常参加に使用しません。</summary>
    /// <returns>実UDP上の故障境界確認です。</returns>
    [Theory]
    [InlineData("hash", "Sync.HashMismatch")]
    [InlineData("truncated", "Sync.ChunkMismatch")]
    [InlineData("instance", "Sync.ManifestMismatch")]
    [InlineData("disconnected", "Sync.ChunkUnconfirmed")]
    [InlineData("reason", "Sync.RemoteRejected")]
    public async Task InvalidReverseTransferNeverImportsOrBecomesReady(string fault, string expectedReason)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-invalid-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = LargeSynchronizationTests.Port();
            string clusterId = Guid.NewGuid().ToString("N");
            SqliteRepository repository = new(Path.Combine(root, "state.db"));
            await using PeerCluster target = LargeSynchronizationTests.Create(repository, root, clusterId, "target", port, false);
            await target.StartAsync().ConfigureAwait(true);
            Guid sourceInstance = Guid.NewGuid();
            UdpTransport source = new(new("source", "127.0.0.1", "127.255.255.255", port, clusterId), sourceInstance, TimeProvider.System);
            await using ConfiguredAsyncDisposable sourceLifetime = ((IAsyncDisposable)source).ConfigureAwait(true);
            source.Initialize(NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id);
            RepositorySnapshot snapshot = new([new("workflow:incoming", "value", "version", NodeValue.FromObject(new string('x', 90000)))], []);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            Guid session = Guid.NewGuid();
            DateTimeOffset expiry = DateTimeOffset.UtcNow.AddMinutes(1);
            source.Received = async (message, _, token) =>
            {
                object response;
                switch (message.Kind)
                {
                    case "sync.open":
                        string reasonFault = "reason";
                        if (string.Equals(fault, reasonFault, StringComparison.Ordinal))
                        {
                            response = new { Reason = "private diagnostic " + new string('x', 10000) };
                            break;
                        }
                        Guid manifestInstance = sourceInstance;
                        string instanceFault = "instance";
                        if (string.Equals(fault, instanceFault, StringComparison.Ordinal))
                        {
                            manifestInstance = Guid.NewGuid();
                        }
                        response = new { Manifest = new { Id = session, SourcePeerId = "source", SourceInstanceId = manifestInstance, TargetPeerId = "target", TargetInstanceId = target.LocalInstanceId, Length = payload.LongLength, Hash = Convert.ToHexString(SHA256.HashData(payload)), ExpiresAt = expiry } };
                        break;
                    case "sync.read":
                        string disconnect = "disconnected";
                        if (string.Equals(fault, disconnect, StringComparison.Ordinal))
                        {
                            return;
                        }
                        long offset = message.Body.GetProperty("Offset").GetInt64();
                        int length = message.Body.GetProperty("Length").GetInt32();
                        byte[] data = payload.AsSpan(checked((int)offset), length).ToArray();
                        string hashFault = "hash";
                        string truncated = "truncated";
                        if (string.Equals(fault, hashFault, StringComparison.Ordinal))
                        {
                            data[0] ^= 1;
                        }
                        if (string.Equals(fault, truncated, StringComparison.Ordinal))
                        {
                            data = data[..^1];
                        }
                        response = new { Chunk = new { Id = session, Offset = offset, Data = data } };
                        break;
                    case "sync.close":
                        response = new { };
                        break;
                    default:
                        return;
                }
                await source.SendAsync(new(Guid.NewGuid(), clusterId, "source", sourceInstance, "reply", "target", message.Id, JsonSerializer.SerializeToElement(response), target.LocalInstanceId), token).ConfigureAwait(false);
            };
            source.Start();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            long announcementSequence = 0;
            while (!string.Equals(target.SynchronizationProblem, expectedReason, StringComparison.Ordinal))
            {
                PeerInfo peer = new("source", sourceInstance, "source", "127.0.0.1", port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                await source.SendAsync(new(Guid.NewGuid(), clusterId, "source", sourceInstance, "hello", null, null, JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "different", "source", null, ++announcementSequence))), timeout.Token).ConfigureAwait(true);
                await Task.Delay(100, timeout.Token).ConfigureAwait(true);
            }
            Assert.Equal(EPeerStatus.Syncing, target.GetPeers().Single(peer => peer.InstanceId == target.LocalInstanceId).Status);
            Assert.Empty(await repository.ListAsync("workflow:incoming", timeout.Token).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
