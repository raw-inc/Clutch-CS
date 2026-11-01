using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>固定セッションの再配送・終了・期限と、操作記録を増やさない読取を検証します。</summary>
public sealed class SynchronizationSessionTests
{
    /// <summary>同じopenを再Importせず、readで期限を延ばさず、解放後も遅延openを拒否します。</summary>
    /// <returns>実UDPと永続保存の観測完了です。</returns>
    [Fact]
    public async Task ReplayedSessionHasOneImportAndReadOnlyFiniteLifetime()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-sync-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int port = LargeSynchronizationTests.Port();
            string clusterId = Guid.NewGuid().ToString("N");
            SqliteRepository repository = new(Path.Combine(root, "state.db"));
            ClusterOptions options = new(clusterId, "target", "target", "127.0.0.1", "127.255.255.255", port, 1, true, root)
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromMilliseconds(500), SyncSessionLifetime = TimeSpan.FromSeconds(1), MaximumSyncSessions = 1,
            };

            // 公開期限の判定だけを試験時計で進め、通信の所要時間と切り離します。
            CollaborationClock clock = new();
            PeerCluster target = new(options, repository, clock) { ReadinessCheck = _ => ValueTask.FromResult(true) };
            await using ConfiguredAsyncDisposable targetLifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
            await target.StartAsync().ConfigureAwait(true);
            Guid instance = Guid.NewGuid();
            UdpTransport source = new(new("source", "127.0.0.1", "127.255.255.255", port, clusterId), instance, TimeProvider.System);
            await using ConfiguredAsyncDisposable sourceLifetime = ((IAsyncDisposable)source).ConfigureAwait(true);
            source.Initialize(NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new RepositorySnapshot([new("workflow:session", "value", "version", NodeValue.FromObject(42))], []));
            int sourceReads = 0;
            source.Received = async (message, _, token) =>
            {
                string read = "sync.read";
                if (!string.Equals(message.Kind, read, StringComparison.Ordinal))
                {
                    return;
                }
                Interlocked.Increment(ref sourceReads);
                Guid id = message.Body.GetProperty("Id").GetGuid();
                long offset = message.Body.GetProperty("Offset").GetInt64();
                int length = message.Body.GetProperty("Length").GetInt32();
                await source.SendAsync(new(Guid.NewGuid(), clusterId, "source", instance, "reply", "target", message.Id,
                    JsonSerializer.SerializeToElement(new { Chunk = new { Id = id, Offset = offset, Data = data.AsSpan(checked((int)offset), length).ToArray() } }), target.LocalInstanceId), token).ConfigureAwait(false);
            };
            source.Start();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            long announcementSequence = 0;
            while (!target.GetPeers().Any(peer => peer.InstanceId == instance))
            {
                PeerInfo peer = new("source", instance, "source", "127.0.0.1", port, EPeerStatus.Syncing, 1, null, DateTimeOffset.UtcNow);
                await source.SendAsync(new(Guid.NewGuid(), clusterId, "source", instance, "hello", null, null, JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "different", null, null, ++announcementSequence))), timeout.Token).ConfigureAwait(true);
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            }
            Func<string, object, Task<JsonElement>> send = async (kind, body) =>
            {
                WireMessage? reply = await source.RequestAsync(new(Guid.NewGuid(), clusterId, "source", instance, kind, "target", null,
                    JsonSerializer.SerializeToElement(body), target.LocalInstanceId), target.LocalInstanceId, timeout.Token).ConfigureAwait(false);
                Assert.NotNull(reply);
                return reply.Body;
            };
            Func<Guid, object> manifest = id => new
            {
                Id = id, SourcePeerId = "source", SourceInstanceId = instance, TargetPeerId = "target", TargetInstanceId = target.LocalInstanceId,
                Length = data.LongLength, Hash = Convert.ToHexString(SHA256.HashData(data)), ExpiresAt = clock.GetUtcNow().AddSeconds(20),
            };
            Func<Guid, Task> read = async id =>
            {
                SyncResponse observed = (await send("sync.read", new { Id = id, Offset = 0L, Length = 1 }).ConfigureAwait(true)).Deserialize<SyncResponse>()!;
                Assert.Null(observed.Reason);
                SyncChunk chunk = Assert.IsType<SyncChunk>(observed.Chunk);
                Assert.Equal(id, chunk.Id);
                Assert.Equal(0, chunk.Offset);
                Assert.Single(chunk.Data);
            };
            Guid firstId = Guid.NewGuid();
            object firstManifest = manifest(firstId);
            JsonElement result = await send("sync.open", firstManifest).ConfigureAwait(true);
            string pending = "Pending";
            while (result.GetProperty(pending).GetBoolean())
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                result = await send("sync.result", firstId).ConfigureAwait(true);
            }
            Guid exported = result.GetProperty("Manifest").GetProperty("Id").GetGuid();
            int reads = sourceReads;
            JsonElement repeated = await send("sync.open", firstManifest).ConfigureAwait(true);
            Assert.Equal(exported, repeated.GetProperty("Manifest").GetProperty("Id").GetGuid());
            Assert.Equal(reads, sourceReads);
            int receipts = (await repository.ExportAsync(timeout.Token).ConfigureAwait(true)).Receipts.Count;
            await read(exported).ConfigureAwait(true);
            await read(exported).ConfigureAwait(true);
            Assert.Equal(receipts, (await repository.ExportAsync(timeout.Token).ConfigureAwait(true)).Receipts.Count);
            _ = await send("sync.close", new { ExportId = exported, ImportId = firstId }).ConfigureAwait(true);
            JsonElement late = await send("sync.open", firstManifest).ConfigureAwait(true);
            Assert.Equal("Sync.SessionUnavailable", late.GetProperty("Reason").GetString());
            Assert.Equal(reads, sourceReads);
            Guid secondId = Guid.NewGuid();
            result = await send("sync.open", manifest(secondId)).ConfigureAwait(true);
            while (result.GetProperty(pending).GetBoolean())
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
                result = await send("sync.result", secondId).ConfigureAwait(true);
            }
            Guid expiryExport = result.GetProperty("Manifest").GetProperty("Id").GetGuid();
            DateTimeOffset expiresAt = result.GetProperty("Manifest").GetProperty("ExpiresAt").GetDateTimeOffset();
            clock.Advance(expiresAt - clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
            await read(expiryExport).ConfigureAwait(true);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            JsonElement expired = await send("sync.read", new { Id = expiryExport, Offset = 0L, Length = 1 }).ConfigureAwait(true);
            Assert.Equal("Sync.SessionUnavailable", expired.GetProperty("Reason").GetString());
            Assert.Equal(42, (await repository.ReadAsync("workflow:session", "value", timeout.Token).ConfigureAwait(true))!.Value!.Json.GetInt32());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
