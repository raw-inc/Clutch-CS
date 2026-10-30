using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>概要キャッシュの障害分離と差分配信を検証します。</summary>
public sealed class DashboardCacheTests
{
    /// <summary>新Coreの一括応答では一往復だけで同じ三種類の状態を得ます。</summary>
    /// <returns>余分な旧照会を出さないことです。</returns>
    [Fact]
    public async Task CombinedSnapshotUsesOneInspectionAndRetainsIdentityChecks()
    {
        PeerInfo peer = Peer();
        int reads = 0;
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            reads++;
            Assert.Equal("dashboard.current", command.Operation);
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                health = Reply(command with { Operation = "health" }, peer),
                status = Reply(command with { Operation = "status" }, peer),
                resource = Reply(command with { Operation = "resources.latest" }, peer),
            }, ContractJson.Options));
        });
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot result = cache.Read("cluster", peer.PeerId)!;
        Assert.Equal(1, reads);
        Assert.NotNull(result.Health);
        Assert.NotNull(result.Status);
        Assert.Single(result.Samples);
        await cache.RefreshAsync("cluster", peer, false);
        Assert.Equal(2, reads);
    }

    /// <summary>旧Coreの機能未対応を毎回問い合わせず、既存経路を維持します。</summary>
    /// <returns>旧版の確認失敗を稼働失敗へ取り違えないことです。</returns>
    [Fact]
    public async Task LegacyFallbackDoesNotProbeTheUnsupportedOperationEveryTick()
    {
        PeerInfo peer = Peer();
        List<string> operations = [];
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            operations.Add(command.Operation);
            return Task.FromResult(Reply(command, peer));
        });
        await cache.RefreshAsync("cluster", peer, false);
        await cache.RefreshAsync("cluster", peer, false);
        Assert.Single(operations, operation => operation is "dashboard.current");
        Assert.Equal(2, operations.Count(operation => operation is "health"));
        Assert.NotNull(cache.Read("cluster", peer.PeerId)!.Health);
    }

    /// <summary>一括取得は同じ実体だけを返し、軽量一覧で履歴や追加Core照会を発生させません。</summary>
    /// <returns>件数・実体・内容の境界を確認します。</returns>
    [Fact]
    public async Task BatchReadsAreBoundedAndHealthOnlyDoesNotReturnHistory()
    {
        PeerInfo peer = Peer();
        int reads = 0;
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Reply(command, peer));
        });
        await cache.RefreshAsync("cluster", peer, false);
        int before = reads;
        DashboardSnapshotQuery query = new("cluster", peer.PeerId, peer.InstanceId, HealthOnly: true);
        DashboardPeerSnapshot snapshot = Assert.Single(cache.ReadMany([query]));
        Assert.NotNull(snapshot.Health);
        Assert.Empty(snapshot.Samples);
        Assert.Null(snapshot.Status);
        Assert.Null(snapshot.Executions);
        Assert.Null(snapshot.Logs);
        Assert.Null(snapshot.NodeExecutions);
        DashboardPeerSnapshot diagnostics = Assert.Single(cache.ReadMany([query with { HealthOnly = false, DiagnosticsOnly = true }]));
        Assert.Empty(diagnostics.Samples);
        Assert.Null(diagnostics.Health);
        Assert.Null(diagnostics.Executions);
        Assert.Null(diagnostics.Logs);
        Assert.NotNull(diagnostics.Status);
        Assert.Equal(before, reads);
        Assert.Empty(cache.ReadMany([query with { InstanceId = Guid.NewGuid() }]));
        Assert.Throws<ArgumentException>(() => cache.ReadMany([query, query]));
        Assert.Throws<ArgumentException>(() => cache.ReadMany(Enumerable.Range(0, 129).Select(_ => query with { InstanceId = Guid.NewGuid() }).ToArray()));
    }

    /// <summary>ログの照会待ちや失敗は、稼働確認と計測の反映を止めません。</summary>
    /// <returns>並行する照会と実体別キャッシュの確認です。</returns>
    [Fact]
    public async Task SlowDiagnosticsDoNotBlockHealthOrClearMeasurements()
    {
        PeerInfo peer = Peer();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using DashboardCache cache = new(() => [Cluster(peer)], async (command, token) =>
        {
            string logsOperation = "logs.query", executionsOperation = "executions.list";
            if (string.Equals(command.Operation, logsOperation, StringComparison.Ordinal) || string.Equals(command.Operation, executionsOperation, StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token).ConfigureAwait(true);
                throw new IOException("Diagnostic read failed.");
            }
            return Reply(command, peer);
        });
        Task diagnostics = cache.RefreshAsync("cluster", peer, true);
        try
        {
            await cache.RefreshAsync("cluster", peer, false);
            DashboardPeerSnapshot first = Assert.IsType<DashboardPeerSnapshot>(cache.Read("cluster", peer.PeerId));
            Assert.NotNull(first.Health);
            Assert.NotNull(first.Status);
            Assert.Single(first.Samples);
            Assert.False(diagnostics.IsCompleted);
            release.SetResult();
            await diagnostics.ConfigureAwait(true);
            DashboardPeerSnapshot after = Assert.IsType<DashboardPeerSnapshot>(cache.Read("cluster", peer.PeerId));
            Assert.Equal(first.CheckedAt, after.CheckedAt);
            Assert.NotNull(after.Health);
            Assert.Equal(first.Samples, after.Samples);
            Assert.Null(after.Logs);
        }
        finally
        {
            release.TrySetResult();
            await diagnostics.ConfigureAwait(true);
        }
    }

    /// <summary>状態詳細や計測が欠測でも独立したhealthの成功を破棄しません。</summary>
    /// <returns>軽量一覧と概要が共有する稼働確認の確認です。</returns>
    [Fact]
    public async Task FailedResourceAndStatusDoNotInvalidateConfirmedHealth()
    {
        PeerInfo peer = Peer();
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) => Task.FromResult(command.Operation switch
        {
            "health" => Reply(command, peer),
            _ => null
        }));
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot result = Assert.IsType<DashboardPeerSnapshot>(cache.Read("cluster", peer.PeerId));
        Assert.NotNull(result.Health);
        Assert.NotNull(result.CheckedAt);
        Assert.Null(result.Status);
        Assert.Empty(result.Samples);
        Assert.NotNull(result.Problem);
    }

    /// <summary>差分は受信済みの点を省き、再読込みと未知のカーソルには有限履歴を返します。</summary>
    /// <returns>差分を統合すると全履歴と一致することの確認です。</returns>
    [Fact]
    public async Task IncrementalReadsAndReloadHaveTheSameHistory()
    {
        PeerInfo peer = Peer();
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) => Task.FromResult(Reply(command, peer)));
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot first = cache.Read("cluster", peer.PeerId)!;
        Guid cursor = Assert.Single(first.Samples).Id;
        Assert.Empty(cache.Read("cluster", peer.PeerId, cursor)!.Samples);
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot delta = cache.Read("cluster", peer.PeerId, cursor)!;
        Assert.Single(delta.Samples);
        DashboardPeerSnapshot full = cache.Read("cluster", peer.PeerId)!;
        Assert.Equal(full.Samples, DashboardSampleHistory.Merge(first.Samples, delta.Samples, peer.InstanceId, DateTimeOffset.UtcNow));
        Assert.Equal(full.Samples, cache.Read("cluster", peer.PeerId, Guid.NewGuid())!.Samples);
        peer = peer with { InstanceId = Guid.NewGuid() };
        Assert.Null(cache.Read("cluster", peer.PeerId, cursor));
        await cache.RefreshAsync("cluster", peer, false);
        Assert.Single(cache.Read("cluster", peer.PeerId, cursor)!.Samples);
    }

    /// <summary>共有説明の配信は明示要求だけに適用し、差分・軽量照会・実体切替を維持します。</summary>
    /// <returns>元のキャッシュを変更せず、旧画面へ同じ応答を返す確認です。</returns>
    [Fact]
    public async Task CompactHistoryPreservesCursorsAndDoesNotChangeLegacyOrHealthResponses()
    {
        PeerInfo peer = Peer();
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) => Task.FromResult(Reply(command, peer)));
        for (int index = 0; index < 8; index++)
        {
            await cache.RefreshAsync("cluster", peer, false);
        }
        DashboardPeerSnapshot original = cache.Read("cluster", peer.PeerId)!;
        DashboardSnapshotQuery query = new("cluster", peer.PeerId, peer.InstanceId);
        DashboardPeerSnapshot compact = Assert.Single(cache.ReadMany([query], compact: true));
        Assert.Empty(compact.Samples);
        Assert.Equal(JsonSerializer.Serialize(original.Samples, ContractJson.Options),
            JsonSerializer.Serialize(DashboardSampleSeries.Decode(compact.SampleSeries!), ContractJson.Options));
        Assert.Equal(original.Samples, cache.Read("cluster", peer.PeerId)!.Samples);
        Assert.Null(Assert.Single(cache.ReadMany([query])).SampleSeries);
        Assert.DoesNotContain("sampleSeries", JsonSerializer.Serialize(original, ContractJson.Options), StringComparison.Ordinal);
        Assert.Equal(original.Samples[^1].Id, Assert.Single(cache.Read("cluster", peer.PeerId, original.Samples[^2].Id, compact: true)!.Samples).Id);
        Assert.Empty(cache.Read("cluster", peer.PeerId, original.Samples[^1].Id, compact: true)!.Samples);
        Assert.Null(Assert.Single(cache.ReadMany([query with { HealthOnly = true }], compact: true)).SampleSeries);
        Assert.Null(Assert.Single(cache.ReadMany([query with { DiagnosticsOnly = true }], compact: true)).SampleSeries);
        Assert.NotNull(cache.Read("cluster", peer.PeerId, Guid.NewGuid(), compact: true)!.SampleSeries);
        peer = peer with { InstanceId = Guid.NewGuid() };
        Assert.Null(cache.Read("cluster", peer.PeerId, compact: true));
        await cache.RefreshAsync("cluster", peer, false);
        Assert.Single(cache.Read("cluster", peer.PeerId, compact: true)!.Samples);
    }

    /// <summary>別実体の返信を採用せず、直前のReadyも現在値として残しません。</summary>
    /// <returns>世代を越えた返信の拒否です。</returns>
    [Fact]
    public async Task OldInstanceCannotConfirmTheCurrentPeer()
    {
        PeerInfo peer = Peer(), replyPeer = peer;
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) => Task.FromResult(Reply(command, replyPeer)));
        await cache.RefreshAsync("cluster", peer, false);
        replyPeer = peer with { InstanceId = Guid.NewGuid() };
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot result = cache.Read("cluster", peer.PeerId)!;
        Assert.Null(result.Health);
        Assert.Null(result.Status);
        Assert.Null(result.CheckedAt);
        Assert.Single(result.Samples);
    }

    /// <summary>配信元へ概要を要求し、旧Coreが全詳細を返す場合も必要な値だけを保持します。</summary>
    /// <returns>後方互換性を維持した送信量削減要求です。</returns>
    [Fact]
    public async Task DashboardRequestsCompactResourcesAndAcceptsLegacyFullResponses()
    {
        PeerInfo peer = Peer();
        ResourceSampleQuery? query = null;
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            string resources = "resources.latest";
            if (!string.Equals(command.Operation, resources, StringComparison.Ordinal))
            {
                return Task.FromResult(Reply(command, peer));
            }
            query = command.Payload!.Json.Deserialize<ResourceSampleQuery>(ContractJson.Options);
            ResourceSample full = new(Guid.NewGuid(), peer.PeerId, DateTimeOffset.UtcNow,
                [new("cpu.usage", "machine", 10, "%"), new("process.cpu", "process", 20, "%")], InstanceId: peer.InstanceId);
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(full, ContractJson.Options));
        });
        await cache.RefreshAsync("cluster", peer, false);
        Assert.True(query!.Summary);
        ResourceSample sample = Assert.Single(cache.Read("cluster", peer.PeerId)!.Samples);
        Assert.Equal("cpu.usage", Assert.Single(sample.Metrics).Name);
    }

    /// <summary>複数Dashboardが同時起動しても、一台の照会burstを有限に保ちます。</summary>
    /// <returns>監視と診断それぞれの同時要求上限です。</returns>
    [Fact]
    public async Task PollingBoundsConcurrentRequestsAcrossManyPeers()
    {
        PeerInfo[] peers = Enumerable.Range(0, 27).Select(index => Peer() with { PeerId = "peer-" + index }).ToArray();
        TaskCompletionSource full = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        object gate = new();
        int fast = 0, details = 0, maximumFast = 0, maximumDetails = 0;
        using DashboardCache cache = new(() => [new("cluster", peers, [], DateTimeOffset.UtcNow)], async (command, token) =>
        {
            bool diagnostic = command.Operation is "executions.list" or "executions.activity" or "logs.query";
            lock (gate)
            {
                if (diagnostic)
                {
                    maximumDetails = Math.Max(maximumDetails, ++details);
                }
                else
                {
                    maximumFast = Math.Max(maximumFast, ++fast);
                }

                // 機能確認後の旧経路と活動ページでは開始段階が異なるため、上限はそれぞれ観測します。
                if (24 <= maximumFast && 4 <= maximumDetails)
                {
                    full.TrySetResult();
                }
            }
            try
            {
                await release.Task.WaitAsync(token).ConfigureAwait(true);
                return null;
            }
            finally
            {
                lock (gate)
                {
                    if (diagnostic)
                    {
                        details--;
                    }
                    else
                    {
                        fast--;
                    }
                }
            }
        });
        await cache.StartAsync(CancellationToken.None);
        try
        {
            await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await cache.StopAsync(CancellationToken.None);
        }
        Assert.InRange(maximumFast, 1, 24);
        Assert.InRange(maximumDetails, 1, 4);
    }

    /// <summary>概要に不要な詳細を除き、グラフ・GPU・指定NICの値と欠測理由を維持します。</summary>
    [Fact]
    public void ProjectionAndMergeKeepOnlyBoundedDashboardData()
    {
        PeerInfo peer = Peer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResourceMetric[] metrics = [new("cpu.usage", "machine", 10, "%"), new("gpu.memory.total", "gpu", 4096, "bytes", Label: "GPU"),
            new("network.received", "selected", null, "bytes", "first sample"), new("network.received", "other", 12, "bytes"),
            new("process.cpu", "process", 20, "%"), new("gpu.engine.usage", "engine", 30, "%")];
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, now, metrics, true, peer.InstanceId, "selected");
        ResourceSample projected = DashboardSampleHistory.Project(sample);
        Assert.Equal(metrics.Take(3), projected.Metrics);
        ResourceSample[] history = Enumerable.Range(0, 200).Select(index => projected with { Id = Guid.NewGuid(), ObservedAt = now.AddSeconds(-index) }).ToArray();
        ResourceSample[] result = DashboardSampleHistory.Merge(history, [history[0], projected with { ObservedAt = now.AddMinutes(-7) }, projected with { InstanceId = Guid.NewGuid() }], peer.InstanceId, now);
        Assert.Equal(121, result.Length);
        Assert.Equal(121, result.Select(item => item.Id).Distinct().Count());
        Assert.Equal(now, result[^1].ObservedAt);
    }

    /// <summary>一意な実体の発見通知を作ります。</summary>
    /// <returns>試験対象です。</returns>
    private static PeerInfo Peer() => new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);

    /// <summary>同じ実体だけを発見中とします。</summary>
    /// <returns>一つのクラスタです。</returns>
    private static DiscoveredCluster Cluster(PeerInfo peer) => new("cluster", [peer], [peer.PeerId], DateTimeOffset.UtcNow);

    /// <summary>管理照会に実際のJSON契約で応答します。</summary>
    /// <returns>状態または計測の返信です。</returns>
    private static JsonElement? Reply(ManagementCommand command, PeerInfo peer) => command.Operation switch
    {
        "health" or "status" => JsonSerializer.SerializeToElement(new { clusterId = "cluster", peer.PeerId, peer.InstanceId, status = EPeerStatus.Ready }, ContractJson.Options),
        "resources.latest" => JsonSerializer.SerializeToElement(new ResourceSample(Guid.NewGuid(), peer.PeerId, DateTimeOffset.UtcNow,
            [new("cpu.usage", "machine", 10, "%")], true, peer.InstanceId), ContractJson.Options),
        _ => null
    };
}
