using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>既知集合を再起動で忘れず、保存不能・過去Readyを通常参加へ混ぜません。</summary>
public sealed class KnownPeerTests
{
    /// <summary>一Peer喪失後の残存再起動でもdegradedの停止指定を失いません。</summary>
    /// <returns>ローカル記録と実UDP再参加の検証の完了。</returns>
    [Fact]
    public async Task RestartKeepsMissingPeerAndDoesNotJournalEveryHeartbeat()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await fixture.StartAsync("c");
        await WaitKnownAsync(fixture.Repositories["b"], "b", peers => peers.Length == 2);
        await fixture.StopAsync("c");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["b"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        await fixture.StopAsync("b");
        await fixture.StartAsync("b");
        Assert.Equal(EPeerStatus.Unavailable, fixture.Peers["b"].GetPeers().Single(peer => string.Equals(peer.PeerId, "c", StringComparison.Ordinal)).Status);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["b"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        Assert.True(new NodeSafetyGate(fixture.Peers["b"]).IsAutomaticallyStopped(ManualSafetyFixture.Node));
        DurableEntry before = (await fixture.Repositories["b"].ReadAsync("peer:b:membership", "known"))!;
        DateTimeOffset observed = fixture.Peers["b"].GetPeers().Single(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal)).LastSeen;
        await ManualSafetyFixture.WaitAsync(() => observed.AddMilliseconds(180) < fixture.Peers["b"].GetPeers().Single(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal)).LastSeen);
        Assert.Equal(before.Version, (await fixture.Repositories["b"].ReadAsync("peer:b:membership", "known"))!.Version);
        Assert.DoesNotContain((await fixture.Repositories["b"].ExportAsync()).Entries, entry => string.Equals(entry.Scope, "peer:b:membership", StringComparison.Ordinal));
    }

    /// <summary>全停止後も他Peerの過去Readyを復元せず、既知喪失を維持します。</summary>
    /// <returns>単体回復後の期待集合の確認です。</returns>
    [Fact]
    public async Task WholeRestartRestoresOthersAsUnavailable()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await fixture.StartAsync("c");
        await WaitKnownAsync(fixture.Repositories["a"], "a", peers => peers.Length == 2);
        await fixture.StopAsync("c");
        await fixture.StopAsync("b");
        await fixture.StopAsync("a");
        await fixture.StartAsync("a");
        Assert.All(fixture.Peers["a"].GetPeers().Where(peer => !string.Equals(peer.PeerId, "a", StringComparison.Ordinal)), peer =>
        {
            Assert.Equal(EPeerStatus.Unavailable, peer.Status);
            Assert.Null(peer.ReadySince);
        });
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Any(peer => peer.Status == EPeerStatus.Ready));
        Assert.Equal(3, fixture.Peers["a"].GetPeers().Count);
    }

    /// <summary>明示離脱は無通信や再起動で喪失に変えず、明示再参加で更新します。</summary>
    /// <returns>離脱の保持と復帰の確認です。</returns>
    [Fact]
    public async Task DetachedPeerSurvivesObserverAndOwnRestartUntilExplicitRejoin()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        await fixture.Peers["b"].DetachAsync();
        await WaitKnownAsync(fixture.Repositories["a"], "a", peers => peers.Any(peer => peer.Status == EPeerStatus.Detached));
        await fixture.StopAsync("b");
        await fixture.StopAsync("a");
        await fixture.StartAsync("a");
        Assert.Equal(EPeerStatus.Detached, fixture.Peers["a"].GetPeers().Single(peer => string.Equals(peer.PeerId, "b", StringComparison.Ordinal)).Status);
        await fixture.StartAsync("b");
        Assert.Equal(EPeerStatus.Detached, fixture.Peers["b"].GetPeers().Single(peer => string.Equals(peer.PeerId, "b", StringComparison.Ordinal)).Status);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Any(peer => peer.InstanceId == fixture.Peers["b"].LocalInstanceId && peer.Status == EPeerStatus.Detached));
        await fixture.Peers["b"].RejoinAsync();
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
    }

    /// <summary>ローカル集合保存不能をUnavailableにし、保存回復後だけReadyへ進みます。</summary>
    /// <returns>失敗時の通常参加拒否と再保存確認です。</returns>
    [Fact]
    public async Task KnownSetWriteFailureBlocksReadyAndMissingRetainedSetBlocksRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "state.db"))) { FailScope = "peer:a:membership" };
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        ClusterOptions options = new(Guid.NewGuid().ToString("N"), "a", "host", "127.0.0.1", "127.255.255.255", port, 1, true, root) { HeartbeatInterval = TimeSpan.FromMilliseconds(80), PeerTimeout = TimeSpan.FromMilliseconds(350) };
        await using PeerCluster first = Create(options, repository);
        await first.StartAsync();
        Assert.Equal(EPeerStatus.Unavailable, Assert.Single(first.GetPeers()).Status);
        repository.FailScope = null;
        await ManualSafetyFixture.WaitAsync(() => Assert.Single(first.GetPeers()).Status == EPeerStatus.Ready);
        DurableEntry known = (await repository.ReadAsync("peer:a:membership", "known"))!;
        await first.StopAsync();
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation(known.Scope, known.Key, known.Version, null)]);
        await using PeerCluster restarted = Create(options with { NewCluster = false }, repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.StartAsync());
    }

    /// <summary>保存済み集合を実際に読めるまで待ちます。</summary>
    /// <returns>ローカル置換の確認完了です。</returns>
    private static async Task WaitKnownAsync(IDurableRepository repository, string id, Func<PeerInfo[], bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        bool complete = false;
        while (!complete)
        {
            DurableEntry? entry = await repository.ReadAsync("peer:" + id + ":membership", "known", deadline.Token).ConfigureAwait(false);
            complete = entry?.Value is not null && condition(entry.Value.Json.GetProperty("Peers").Deserialize<PeerInfo[]>()!);
            if (!complete)
            {
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>ローカル書込失敗だけを注入するPeerを作ります。</summary>
    /// <returns>検証用Peerです。</returns>
    private static PeerCluster Create(ClusterOptions options, IDurableRepository repository) => new(options, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
}
