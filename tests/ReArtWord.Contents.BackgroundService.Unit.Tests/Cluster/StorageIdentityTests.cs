using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>同じ保存先の誤った再利用とNICの暗黙変更を拒否します。</summary>
public sealed class StorageIdentityTests
{
    /// <summary>所有者を変更してもinitializeは既存資産の意味を変更できません。</summary>
    /// <returns>保存先の起動検証完了。</returns>
    [Theory]
    [InlineData("cluster")]
    [InlineData("peer")]
    [InlineData("host")]
    [InlineData("nic")]
    public async Task ChangedIdentityCannotStartAsFreshCluster(string change)
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        ClusterOptions options = Options();
        await using (PeerCluster first = Create(options, repository))
        {
            await first.StartAsync();
            Assert.Equal(EPeerStatus.Ready, Assert.Single(first.GetPeers()).Status);
        }
        switch (change)
        {
            case "cluster": options = options with { Id = "other" }; break;
            case "peer": options = options with { PeerId = "other" }; break;
            case "host": options = options with { HostId = "other" }; break;
            case "nic":
                DurableEntry original = (await repository.ReadAsync("peer:bootstrap", "identity"))!;
                await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("peer:bootstrap", "identity", original.Version,
                    NodeValue.FromObject(new { clusterId = options.Id, peerId = options.PeerId, hostId = options.HostId, interfaceId = "missing-nic", address = options.BindAddress }))]);
                break;
        }
        await using PeerCluster second = Create(options, repository);
        string nic = "nic";
        if (string.Equals(change, nic, StringComparison.Ordinal))
        {
            await second.StartAsync();
            await Task.Delay(100);
            Assert.NotEqual(EPeerStatus.Ready, Assert.Single(second.GetPeers()).Status);
            Assert.Equal("missing-nic", (await repository.ReadAsync("peer:bootstrap", "identity"))!.Value!.Json.GetProperty("interfaceId").GetString());
            return;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => second.StartAsync());
        Assert.NotEqual(EPeerStatus.Ready, Assert.Single(second.GetPeers()).Status);
    }

    /// <summary>既存共有値にidentityがなければ無条件の新規初期化を拒否します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task UnownedAssetsCannotBeAdoptedImplicitly()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflows", "old", null, NodeValue.FromObject("asset"))]);
        await using PeerCluster cluster = Create(Options(), repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => cluster.StartAsync());
        Assert.Equal("asset", (await repository.ReadAsync("workflows", "old"))!.Value!.GetString());
    }

    /// <summary>同じ所有者とNICは保存済み復旧点から再起動できます。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task SameIdentityRestartsWithoutChangingBinding()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        ClusterOptions options = Options();
        await using (PeerCluster first = Create(options, repository))
        {
            await first.StartAsync();
        }
        string version = (await repository.ReadAsync("peer:bootstrap", "identity"))!.Version;
        await using PeerCluster second = Create(options, repository);
        await second.StartAsync();
        Assert.Equal(version, (await repository.ReadAsync("peer:bootstrap", "identity"))!.Version);
    }

    /// <summary>実ループバック通信へ接続します。</summary>
    /// <returns>必要条件を満たしたPeerです。</returns>
    private static PeerCluster Create(ClusterOptions options, IDurableRepository repository) => new(options, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };

    /// <summary>衝突しないUDPポートを確保します。</summary>
    /// <returns>独立したクラスタ設定です。</returns>
    private static ClusterOptions Options()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath());
    }
}
