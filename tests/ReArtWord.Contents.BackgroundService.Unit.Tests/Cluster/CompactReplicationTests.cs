using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>複製の確認本文を小さくしても、永続結果と旧要求との互換性を保つことを検証します。</summary>
public sealed class CompactReplicationTests : IDisposable
{
    private readonly string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "contents-compact-replication-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>旧版の本文付きコミットと新しいIDだけのコミットで、同じ完全な永続結果を残します。</summary>
    /// <returns>小さいACKと、内容照合に使う完全な元操作の区別です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhaseAcknowledgementsRemainSmallAndQueriesRetainTheFullReceiptAsync(bool legacyCommit)
    {
        SqliteRepository repository = new(Path.Combine(directory, "state.db"));
        await using PeerCluster peer = LargeSynchronizationTests.Create(repository, directory, Guid.NewGuid().ToString("N"), "peer", LargeSynchronizationTests.Port(), true);
        using ClusterReplication replication = new(peer, repository);
        Guid operation = Guid.NewGuid();
        string text = new('x', 256 * 1024);
        IReadOnlyList<DurableMutation> mutations = await repository.StampAsync([new("configuration", "large", null, NodeValue.FromObject(text))]).ConfigureAwait(true);
        ReplicationRequest request = new(operation, mutations);
        DurableReceipt prepared = (await replication.ReceiveAsync("prepare", JsonSerializer.SerializeToElement(request)).ConfigureAwait(true)).Deserialize<DurableReceipt>()!;
        Assert.Equal(EOutcome.Succeeded, prepared.Outcome);
        ReplicationRequest commit = new(operation, []);
        if (legacyCommit)
        {
            commit = request;
        }
        JsonElement reply = await replication.ReceiveAsync("commit", JsonSerializer.SerializeToElement(commit)).ConfigureAwait(true);
        DurableReceipt acknowledged = reply.Deserialize<DurableReceipt>()!;
        Assert.Equal(EOutcome.Succeeded, acknowledged.Outcome);
        Assert.Equal(operation, acknowledged.OperationId);
        Assert.Equal(prepared.Fingerprint, acknowledged.Fingerprint);
        Assert.Empty(acknowledged.Entries);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(reply).Length, 1, 512);
        DurableReceipt stored = (await repository.ReadReceiptAsync(operation).ConfigureAwait(true))!;
        Assert.Equal(text, Assert.Single(stored.Entries).Value!.Json.GetString());
        DurableReceipt queried = (await replication.ReceiveAsync("query", JsonSerializer.SerializeToElement(new ReplicationRequest(operation, []))).ConfigureAwait(true)).Deserialize<DurableReceipt>()!;
        Assert.Equal(EOutcome.Succeeded, queried.Outcome);
        Assert.Equal(text, Assert.Single(queried.Entries).Value!.Json.GetString());
        Assert.Equal(stored.Fingerprint, queried.Fingerprint);
        DurableReceipt repeated = (await replication.ReceiveAsync("commit", JsonSerializer.SerializeToElement(commit)).ConfigureAwait(true)).Deserialize<DurableReceipt>()!;
        Assert.Equal(acknowledged.OperationId, repeated.OperationId);
        Assert.Equal(acknowledged.Fingerprint, repeated.Fingerprint);
        Assert.Equal(acknowledged.Outcome, repeated.Outcome);
        Assert.Empty(repeated.Entries);
        Assert.Equal(stored.Entries[0].Version, (await repository.ReadAsync("configuration", "large").ConfigureAwait(true))!.Version);
    }

    /// <summary>実UDPで準備にだけ本文を送り、確定段階で同じ大きなWF本文を再転送しません。</summary>
    /// <returns>全Peerの保存確認と、実際に送られた確定要求の確認です。</returns>
    [Fact]
    public async Task CommitDatagramsContainOnlyThePreparedOperationIdentityAsync()
    {
        SqliteRepository local = new(Path.Combine(directory, "local.db")), remote = new(Path.Combine(directory, "remote.db"));
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        await using PeerCluster first = LargeSynchronizationTests.Create(local, directory, cluster, "first", port, true, preferRequestStream: false);
        await using PeerCluster second = LargeSynchronizationTests.Create(remote, directory, cluster, "second", port, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2
            || second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        ConcurrentQueue<WireMessage> observed = new();
        UdpTransport observer = new(new("second", "127.0.0.1", "127.255.255.255", port, cluster), second.LocalInstanceId, TimeProvider.System)
        {
            Received = (message, _, _) =>
            {
                observed.Enqueue(message);
                return Task.CompletedTask;
            }
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = ((IAsyncDisposable)observer).ConfigureAwait(true);
        observer.Initialize(NetworkInterface.GetAllNetworkInterfaces().Single(network => network.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id);
        observer.Start();
        Guid operation = Guid.NewGuid();
        string text = new('x', 128 * 1024), prepare = "prepare", commit = "commit";
        DurableReceipt result = await first.ReplicateAsync(operation, [new("configuration", "large", null, NodeValue.FromObject(text))], timeout.Token).ConfigureAwait(true);
        while (!observed.Any(message => string.Equals(message.Kind, commit, StringComparison.Ordinal)))
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(text, Assert.Single(result.Entries).Value!.Json.GetString());
        Assert.Equal(text, (await remote.ReadAsync("configuration", "large", timeout.Token).ConfigureAwait(true))!.Value!.Json.GetString());

        // UDPは確認応答まで同じ準備を再送できます。回数ではなく、届いた全件の内容を検証します。
        WireMessage[] compressed = observed.Where(message => string.Equals(message.Kind, ReplicationPreparationPayload.Kind, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(compressed);
        Assert.All(compressed, message =>
        {
            Assert.Equal(operation, ReplicationPreparationPayload.Read(message.Body, 1048576).Id);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(message).Length, 1, 4096);
        });
        Assert.DoesNotContain(observed, message => string.Equals(message.Kind, prepare, StringComparison.Ordinal));
        Assert.All(observed.Where(message => string.Equals(message.Kind, commit, StringComparison.Ordinal)), message =>
        {
            ReplicationRequest sent = message.Body.Deserialize<ReplicationRequest>()!;
            Assert.Equal(operation, sent.Id);
            Assert.Empty(sent.Mutations);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(message).Length, 1, 864);
        });
    }

    /// <summary>試験だけが所有するSQLite保存先を解放します。</summary>
    public void Dispose() => Directory.Delete(directory, true);
}
