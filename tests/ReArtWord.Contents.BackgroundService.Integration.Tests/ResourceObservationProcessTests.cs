using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの採取から別Peerでの低負荷選択まで、経過時間の輸送を確認します。</summary>
public sealed class ResourceObservationProcessTests
{
    /// <summary>別プロセスの計測を使用し、更新前Coreとの混在でも明示確認した旧形式を維持します。</summary>
    /// <returns>二Coreの実採取・通信・比較値と、新形式の経過時間です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectedResourcesRemainSelectableAcrossCoreProcessesAsync(bool previousPeer)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? previous = null;
        if (previousPeer)
        {
            previous = Environment.GetEnvironmentVariable("CONTENTS_OBSERVATION_COMPAT_CORE");
        }
        CorePeer first = new(cluster, "a", port, true, "host-a", 100), second = new(cluster, "b", port, false, "host-b", 90, previous);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        string probePath = Path.Combine(first.DirectoryPath, "probe");
        ClusterOptions options = new(cluster, "probe", "probe-host", "127.0.0.1", "127.255.255.255", port, 0, false, probePath)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(3) };
        PeerCluster probe = new(options, new SqliteRepository(Path.Combine(probePath, "state.db"))) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable probeLifetime = probe.ConfigureAwait(true);
        ResourceSelectionExchange exchange = new(probe, () => null);
        await probe.StartAsync(timeout.Token).ConfigureAwait(true);
        IReadOnlyDictionary<string, ResourceSample> samples = new Dictionary<string, ResourceSample>();
        await CorePeer.WaitAsync(async token =>
        {
            await exchange.RefreshAsync(token).ConfigureAwait(true);
            samples = exchange.Capture();
            PeerInfo[] peers = probe.GetPeers().Where(peer => peer.Status == EPeerStatus.Ready && peer.InstanceId != probe.LocalInstanceId).ToArray();
            return peers.Length == 2 && peers.All(peer => PcResourceComparison.Read(peer, samples.GetValueOrDefault(peer.PeerId),
                new(EPcResource.Ram), DateTimeOffset.UtcNow).Reason is null);
        }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, samples.Count);
        Assert.All(samples.Values, sample => Assert.NotNull(sample.Age));
        JsonElement bundle = await first.GetAsync<JsonElement>("dashboard.current", timeout.Token).ConfigureAwait(true);
        Assert.InRange(bundle.GetProperty("resourceAgeTicks").GetInt64(), 0, TimeSpan.FromSeconds(10).Ticks);
    }
}
