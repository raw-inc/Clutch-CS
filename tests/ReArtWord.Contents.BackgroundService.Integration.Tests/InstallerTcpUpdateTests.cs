using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>参加しない更新クライアントから、実CoreへTCPで更新を配送します。</summary>
public sealed class InstallerTcpUpdateTests(ITestOutputHelper output)
{
    /// <summary>転送、保存検証、入替、新実体の起動までを実通信で確認します。</summary>
    /// <returns>本番と異なるポートと配置先での更新確認です。</returns>
    [Fact]
    public async Task ObserverUpdatesCoreOverTcpWithoutJoiningCluster()
    {
        string root = Path.Combine(Path.GetTempPath(), "clutch-tcp-" + Guid.NewGuid().ToString("N"));
        string payload = Path.Combine(root, "release");
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        ReleaseManifest old = await RollingUpdatePeer.PackageAsync(payload, timeout.Token);
        ReleaseManifest release = old with { ReleaseId = "new" };
        string previousPackage = Environment.GetEnvironmentVariable("CONTENTS_TEST_UPDATE_PREVIOUS_PACKAGE") ?? payload;
        if (!string.Equals(previousPackage, payload, StringComparison.Ordinal))
        {
            old = await LocalUpdateExecutor.ReadManifestAsync(previousPackage, timeout.Token);
            release = release with { CompatibleReleaseIds = [old.ReleaseId, release.ReleaseId] };
        }
        string cluster = Guid.NewGuid().ToString("N");
        const string peerId = "tcp-target";
        RollingUpdatePeer peer = new(root, cluster, peerId, CorePeer.AvailablePort(), previousPackage, old, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        Guid previous = await peer.StartAsync(timeout.Token);
        Guid operationId = Guid.NewGuid();
        ClusterUpdateRequest request = new(operationId, release, payload, [peerId], EUpdateFailurePolicy.RollbackSuccessful);
        ClusterUpdateJob job = new(request, new Dictionary<string, Guid>(StringComparer.Ordinal) { [peerId] = previous },
            peer.Binding, cluster, Path.Combine(root, "operations.json"), "json", "installer");
        UdpUpdatePeerTransport transport = new(job);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable transportLifetime = transport.ConfigureAwait(true);
        List<(long Sent, long Total)> progress = [];
        transport.TransferProgress = (_, sent, total) => progress.Add((sent, total));
        Stopwatch watch = Stopwatch.StartNew();
        ClusterUpdateStatus result = await new ClusterUpdateCoordinator(new JsonFileRepository(job.RepositoryPath), transport).ExecuteAsync(request, timeout.Token);
        UpdatePeerResult? local = await peer.ReadUpdateResultAsync(operationId).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(new { result.Outcome, result.Reason, result.Peers, Local = local }));
        Assert.True(File.Exists(Path.Combine(root, cluster, peerId, "staging", "operations", operationId.ToString("N"), "stream-reserved")));
        Assert.Contains(progress, value => 0 < value.Sent && value.Sent < value.Total);
        Assert.Equal(progress[^1].Total, progress[^1].Sent);
        JsonElement health = await peer.HealthAsync(timeout.Token);
        Assert.True(health.GetProperty("ready").GetBoolean());
        Assert.NotEqual(previous, health.GetProperty("instanceId").GetGuid());
        output.WriteLine("TCP update: {0:N0} bytes, {1:N2} seconds including restart.", progress[^1].Total, watch.Elapsed.TotalSeconds);
        output.WriteLine("Previous release: {0}", old.ReleaseId);
    }
}
