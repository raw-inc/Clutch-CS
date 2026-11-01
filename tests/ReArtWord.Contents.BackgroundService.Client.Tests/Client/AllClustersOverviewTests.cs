using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>選択不要の全クラスタ観測が対象と取得範囲を混同しないことを検証します。</summary>
public sealed class AllClustersOverviewTests
{
    private readonly EditorTestClock clock = new();

    /// <summary>低速経路でも履歴を削らず、小さい本文と有限並列で初回表示を完了します。</summary>
    /// <returns>全履歴の保持、差分カーソル、同時取得数の確認です。</returns>
    [Fact]
    public async Task FullHistoryLoadsInSmallBatchesAndRetainsDeltaCursors()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource overlapping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset now = clock.GetUtcNow();
        DiscoveredCluster[] catalog = Enumerable.Range(0, 12).Select(index => Cluster(index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), [Peer("peer")])).ToArray();
        int active = 0, maximum = 0, calls = 0, cursors = 0;
        object gate = new();
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string discovery = "/client/clusters";
            if (string.Equals(request.RequestUri!.AbsolutePath, discovery, StringComparison.Ordinal))
            {
                return Json(catalog);
            }
            string names = "/api/workflows/query";
            if (string.Equals(request.RequestUri.AbsolutePath, names, StringComparison.Ordinal))
            {
                return await ResponseAsync(request, catalog, Guid.NewGuid(), now, token).ConfigureAwait(false);
            }
            DashboardSnapshotQuery[] queries = JsonSerializer.Deserialize<DashboardSnapshotQuery[]>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
            Assert.InRange(queries.Length, 1, 4);
            lock (gate)
            {
                maximum = Math.Max(maximum, ++active);
                calls++;
                cursors += queries.Count(query => query.After is not null);
                if (2 <= active)
                {
                    overlapping.TrySetResult();
                }
            }
            try
            {
                // 2件目の開始まで応答を保留し、実行速度に依存せず並行取得を確認します。
                await overlapping.Task.WaitAsync(token).ConfigureAwait(false);
                using HttpResponseMessage original = await ResponseAsync(request, catalog, Guid.NewGuid(), now, token).ConfigureAwait(false);
                DashboardPeerSnapshot[] snapshots = (await original.Content.ReadFromJsonAsync<DashboardPeerSnapshot[]>(ContractJson.Options, token).ConfigureAwait(false))!;
                return Json(snapshots.Select(snapshot => snapshot with
                {
                    Samples = Enumerable.Range(0, 121).Select(index => snapshot.Samples[0] with { Id = Guid.NewGuid(), ObservedAt = now.AddSeconds((index - 120) * 2) }).ToArray()
                }).ToArray());
            }
            finally
            {
                lock (gate)
                {
                    active--;
                }
            }
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync(timeout.Token);
        Assert.Equal(3, calls);
        Assert.InRange(maximum, 2, 3);
        Assert.Equal(12, overview.Peers.Count);
        Assert.All(overview.Peers, item => Assert.Equal(121, item.Peer.Samples.Count));
        await overview.RefreshAsync(timeout.Token);
        Assert.Equal(12, cursors);
        Assert.All(catalog, cluster => Assert.Equal(("Ready", 1, 1), HealthCounts(overview, cluster, clock.GetUtcNow())));
    }

    /// <summary>一組の本文取得失敗が、成功した他クラスタの稼働確認を消しません。</summary>
    /// <returns>失敗範囲と成功範囲を区別した表示です。</returns>
    [Fact]
    public async Task FailedBatchDoesNotInvalidateOtherClusters()
    {
        DiscoveredCluster[] catalog = Enumerable.Range(0, 8).Select(index => Cluster(index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), [Peer("peer")])).ToArray();
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string batch = "/client/dashboard/batch", failed = "00";
            if (string.Equals(request.RequestUri!.AbsolutePath, batch, StringComparison.Ordinal))
            {
                DashboardSnapshotQuery[] queries = JsonSerializer.Deserialize<DashboardSnapshotQuery[]>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
                if (queries.Any(query => string.Equals(query.ClusterId, failed, StringComparison.Ordinal)))
                {
                    return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
                }
            }
            return await ResponseAsync(request, catalog, Guid.NewGuid(), clock.GetUtcNow(), token).ConfigureAwait(false);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync();
        Assert.NotNull(overview.Problem);
        Assert.All(catalog.Take(4), cluster => Assert.Equal(("Unconfirmed", 0, 0), HealthCounts(overview, cluster, clock.GetUtcNow())));
        Assert.All(catalog.Skip(4), cluster => Assert.Equal(("Ready", 1, 1), HealthCounts(overview, cluster, clock.GetUtcNow())));
    }

    /// <summary>全PC再起動後も開いた画面が新実体を取得し、旧実体の値を混ぜず復帰します。</summary>
    /// <returns>同じ観測器を維持した消失・再発見後の実測です。</returns>
    [Fact]
    public async Task OpenDashboardRecoversAfterEveryPeerOnOneHostRestarts()
    {
        DiscoveredCluster[] catalog = Enumerable.Range(0, 7).Select(index => Cluster(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [Peer("manage") with { HostId = "manage" }, Peer("server") with { HostId = "server" }])).ToArray();
        using ClientHttpHandler handler = new((request, token) => ResponseAsync(request, catalog, Guid.NewGuid(), clock.GetUtcNow(), token));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync();
        string server = "server";
        Guid[] previous = overview.Peers.Where(item => string.Equals(item.Peer.Peer.HostId, server, StringComparison.Ordinal)).Select(item => item.Peer.Peer.InstanceId).ToArray();
        catalog = catalog.Select(cluster => cluster with
        {
            Peers = cluster.Peers.Select(peer =>
        {
            if (string.Equals(peer.HostId, server, StringComparison.Ordinal))
            {
                return peer with { InstanceId = Guid.NewGuid() };
            }
            return peer;
        }).ToArray()
        }).ToArray();
        for (int index = 0; index < 3; index++)
        {
            await overview.RefreshAsync();
            OverviewPeer[] current = overview.Peers.Select(item => item.Peer).Where(peer => string.Equals(peer.Peer.HostId, server, StringComparison.Ordinal) && !previous.Contains(peer.Peer.InstanceId)).ToArray();
            Assert.Equal(7, current.Length);
            Assert.Equal(14, overview.KnownPeers);
            Assert.All(overview.Clusters, cluster => Assert.Equal(("Ready", 2, 2), HealthCounts(overview, cluster, clock.GetUtcNow())));
            Assert.All(current, peer =>
            {
                Assert.Null(peer.Problem);
                Assert.Equal(peer.Peer.InstanceId, peer.Latest!.InstanceId);
                Assert.All(peer.Samples, sample => Assert.Equal(peer.Peer.InstanceId, sample.InstanceId));
            });
        }
    }

    /// <summary>同名Peerと同じ実行IDが別クラスタにあっても、選択を変更せず別々に数えます。</summary>
    /// <returns>既存APIを使った明示対象の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnselectedAndSelectedClientsCanObserveClustersWithoutChangingSelection(bool selected)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Guid execution = Guid.NewGuid();
        DiscoveredCluster[] catalog = [Cluster("a", [Peer("same")]), Cluster("b", [Peer("same")])];
        using ClientHttpHandler handler = new((request, token) => ResponseAsync(request, catalog, execution, now, token));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client, new(clock));
        if (selected)
        {
            api.SelectedClusterId = "untouched";
            api.SelectedPeerId = "chosen";
        }
        string? beforeCluster = api.SelectedClusterId;
        string? beforePeer = api.SelectedPeerId;
        AllClustersTelemetry overview = new(api);
        await overview.RefreshAsync();
        await overview.RefreshAsync();
        Assert.Equal(beforeCluster, api.SelectedClusterId);
        Assert.Equal(beforePeer, api.SelectedPeerId);
        Assert.Equal(2, overview.Peers.Count);
        Assert.Single(overview.Peers.Select(item => item.Peer.Peer.HostId).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, overview.Observations.Count);
        Assert.All(overview.Observations, item => Assert.Equal("表示名 / " + item.Key, item.Value.WorkflowName("workflow")));
        Assert.All(overview.Observations, item => Assert.Equal(1, Assert.Single(item.Value.Activity(now.AddSeconds(2))).Completed));
        Assert.All(overview.Peers, item => Assert.NotNull(item.Peer.Latest));
        Assert.All(overview.Clusters, cluster => Assert.Equal(("Ready", 1, 1), HealthCounts(overview, cluster, clock.GetUtcNow())));
        Assert.All(overview.Clusters, cluster => Assert.Equal("Unconfirmed", overview.Health(cluster, clock.GetUtcNow().AddSeconds(16)).State));
        Assert.Equal("Needs attention", overview.Health(catalog[0] with { CoordinatorCandidates = ["same", "other"] }, clock.GetUtcNow()).State);
    }

    /// <summary>多数クラスタでも現在の128Peerだけを取得し、次ページへ進めます。</summary>
    /// <returns>クラスタ数に比例した無制限定期照会をしない確認です。</returns>
    [Fact]
    public async Task AllClustersPageCapsPollingAcrossClusterBoundaries()
    {
        DiscoveredCluster[] catalog = Enumerable.Range(0, 129).Select(index => Cluster(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), [Peer("same")])).ToArray();
        ConcurrentDictionary<string, byte> requested = new(StringComparer.Ordinal);
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string resourcePath = "/client/dashboard/batch";
            if (string.Equals(request.RequestUri!.AbsolutePath, resourcePath, StringComparison.Ordinal))
            {
                DashboardSnapshotQuery[] queries = JsonSerializer.Deserialize<DashboardSnapshotQuery[]>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!;
                foreach (DashboardSnapshotQuery query in queries)
                {
                    requested.TryAdd(query.ClusterId, 0);
                }
            }
            return await ResponseAsync(request, catalog, Guid.NewGuid(), clock.GetUtcNow(), token).ConfigureAwait(false);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync();
        Assert.Equal(128, requested.Count);
        Assert.Equal(128, overview.Peers.Count);
        Assert.Equal(129, overview.KnownPeers);
        Assert.Equal("Unconfirmed", overview.Health(catalog[128], clock.GetUtcNow()).State);
        overview.NextPage();
        await overview.RefreshAsync();
        Assert.Single(overview.Peers);
        Assert.Equal(129, requested.Count);
        Assert.Single(overview.Observations);
    }

    /// <summary>二台が七クラスタに参加しても全十四Peerの状態を同じページで確認します。</summary>
    /// <returns>実体照合済みReadyと全クラスタの取得範囲の確認です。</returns>
    [Fact]
    public async Task SevenClustersWithTwoHostsVerifyEveryPeerOnOnePage()
    {
        DiscoveredCluster[] catalog = Enumerable.Range(0, 7).Select(index => Cluster(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            [Peer("peer-a") with { HostId = "host-a" }, Peer("peer-b") with { HostId = "host-b" }])).ToArray();
        using ClientHttpHandler handler = new((request, token) => ResponseAsync(request, catalog, Guid.NewGuid(), clock.GetUtcNow(), token));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync();
        Assert.Equal(14, overview.KnownPeers);
        Assert.Equal(14, overview.Peers.Count);
        Assert.Equal(7, overview.Observations.Count);
        Assert.All(overview.Clusters, cluster => Assert.Equal(("Ready", 2, 2), HealthCounts(overview, cluster, clock.GetUtcNow())));
        Assert.Equal(2, overview.Peers.Select(item => item.Peer.Peer.HostId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>発見していたクラスタの消失を短時間は未確認として残し、Readyを捏造しません。</summary>
    /// <returns>停止観測と古い実測の区別です。</returns>
    [Fact]
    public async Task MissingClusterRemainsUnavailableInsteadOfReady()
    {
        DiscoveredCluster cluster = Cluster("a", [Peer("same")]);
        bool missing = false;
        using ClientHttpHandler handler = new((request, token) =>
        {
            string discoveryPath = "/client/clusters";
            if (missing)
            {
                if (string.Equals(request.RequestUri!.AbsolutePath, discoveryPath, StringComparison.Ordinal))
                {
                    return Task.FromResult(Json(Array.Empty<DiscoveredCluster>()));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
            }
            return ResponseAsync(request, [cluster], Guid.NewGuid(), clock.GetUtcNow(), token);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync();
        Assert.NotNull(Assert.Single(overview.Peers).Peer.Latest);
        missing = true;
        await overview.RefreshAsync();
        Assert.Equal(EPeerStatus.Unavailable, Assert.Single(overview.Peers).Peer.Peer.Status);
        Assert.NotNull(Assert.Single(overview.Peers).Peer.Problem);
        Assert.Equal(("Unconfirmed", 0, 0), HealthCounts(overview, Assert.Single(overview.Clusters), clock.GetUtcNow()));
    }

    /// <summary>発見通知がReadyでも、今回の状態照会が準備中ならReady表示にしません。</summary>
    /// <returns>ハートビートと実照会の区別です。</returns>
    [Fact]
    public async Task HealthUsesConfirmedHealthInsteadOfReadyAnnouncement()
    {
        DiscoveredCluster cluster = Cluster("current", [Peer("same")]);
        using ClientHttpHandler handler = new((request, token) =>
        {
            string statusPath = "/client/dashboard/batch";
            if (string.Equals(request.RequestUri!.AbsolutePath, statusPath, StringComparison.Ordinal))
            {
                PeerInfo peer = cluster.Peers[0];
                return Task.FromResult(Json(new[] { new DashboardPeerSnapshot(cluster.ClusterId, peer, clock.GetUtcNow(), null,
                    JsonSerializer.SerializeToElement(new PeerHealth(cluster.ClusterId, peer.PeerId, peer.InstanceId, EPeerStatus.Syncing, null), ContractJson.Options), []) }));
            }
            return ResponseAsync(request, [cluster], Guid.NewGuid(), clock.GetUtcNow(), token);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry overview = new(new(client, new(clock)));
        await overview.RefreshAsync();
        Assert.Equal(("Needs attention", 0, 1), HealthCounts(overview, cluster, clock.GetUtcNow()));
    }

    /// <summary>状態詳細が欠測でもクラスタ概要とピア一覧の稼働判定を一致させます。</summary>
    /// <returns>異なる表示が同じ稼働確認で一致することの確認です。</returns>
    [Theory]
    [InlineData(EPeerStatus.Ready, "Ready")]
    [InlineData(EPeerStatus.Syncing, "Needs attention")]
    public async Task AllClustersAndPeerListUseTheSameHealthWithoutDetailedStatus(EPeerStatus state, string expected)
    {
        PeerInfo peer = Peer("same");
        DiscoveredCluster cluster = Cluster("shared", [peer]);
        PeerHealth health = new(cluster.ClusterId, peer.PeerId, peer.InstanceId, state, "version");
        using ClientHttpHandler handler = new((request, token) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/client/dashboard/batch" => Json(new[] { new DashboardPeerSnapshot(cluster.ClusterId, peer, clock.GetUtcNow(), null,
                JsonSerializer.SerializeToElement(health, ContractJson.Options), []) }),
            "/client/dashboard/health" => Json(health),
            _ => Json(new[] { cluster })
        }));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client, new(clock));
        AllClustersTelemetry overview = new(api);
        PeerListTelemetry list = new(api);
        await overview.RefreshAsync();
        await list.RefreshAsync();
        Assert.Equal(expected, overview.Health(cluster, clock.GetUtcNow()).State);
        Assert.Equal(state, Assert.Single(list.Rows).Health!.Status);
        Assert.Equal(state, Assert.Single(overview.Peers).Peer.VerifiedStatus);
    }

    /// <summary>時間値に依存しない表示集計だけを比較します。</summary>
    /// <returns>稼働確認と今回確認した件数です。</returns>
    private static (string State, int Ready, int Verified) HealthCounts(AllClustersTelemetry overview, DiscoveredCluster cluster, DateTimeOffset now)
    {
        (string State, int Ready, int Verified, DateTimeOffset? CheckedAt, HistorySynchronizationStatus? History) health = overview.Health(cluster, now);
        return (health.State, health.Ready, health.Verified);
    }

    /// <summary>同じ実PCに所属する実体を用意します。</summary>
    /// <returns>当該Core 実体の通知です。</returns>
    private PeerInfo Peer(string id) => new(id, Guid.NewGuid(), "physical-pc", "127.0.0.1", 1234, EPeerStatus.Ready, 0, clock.GetUtcNow(), clock.GetUtcNow());

    /// <summary>発見APIが返すクラスタ情報です。</summary>
    /// <returns>選択とは独立した一覧項目です。</returns>
    private DiscoveredCluster Cluster(string id, PeerInfo[] peers) => new(id, peers, [peers[0].PeerId], clock.GetUtcNow());

    /// <summary>明示ヘッダーのCluster/Peerと一致する実測・履歴だけを返します。</summary>
    /// <returns>各読取りAPIの応答です。</returns>
    private static async Task<HttpResponseMessage> ResponseAsync(HttpRequestMessage request, IReadOnlyList<DiscoveredCluster> catalog, Guid execution, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        string discovery = "/client/clusters";
        if (string.Equals(request.RequestUri!.AbsolutePath, discovery, StringComparison.Ordinal))
        {
            return Json(catalog);
        }
        string names = "/api/workflows/query";
        if (string.Equals(request.RequestUri.AbsolutePath, names, StringComparison.Ordinal))
        {
            string cluster = Assert.Single(request.Headers.GetValues("X-Contents-Cluster"));
            string peer = Assert.Single(request.Headers.GetValues("X-Contents-Peer"));
            Assert.Contains(catalog, item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal) && item.Peers.Any(value => string.Equals(value.PeerId, peer, StringComparison.Ordinal)));
            WorkflowListQuery query = (await request.Content!.ReadFromJsonAsync<WorkflowListQuery>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
            Assert.Equal("workflow", Assert.Single(query.Ids!));
            return Json(new WorkflowListPage([new("workflow", "表示名 / " + cluster, "r", "r", now, 1, true, "hash")], null));
        }
        Assert.Equal("/client/dashboard/batch", request.RequestUri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, request.Method);
        DashboardSnapshotQuery[] queries = (await request.Content!.ReadFromJsonAsync<DashboardSnapshotQuery[]>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
        return Json(queries.Select(query =>
        {
            DiscoveredCluster cluster = catalog.Single(item => string.Equals(item.ClusterId, query.ClusterId, StringComparison.Ordinal));
            PeerInfo peer = cluster.Peers.Single(item => string.Equals(item.PeerId, query.PeerId, StringComparison.Ordinal));
            return new DashboardPeerSnapshot(cluster.ClusterId, peer, now,
                JsonSerializer.SerializeToElement(new ServiceStatus(peer.PeerId, peer.InstanceId, peer.PeerId, EPeerStatus.Ready, cluster.Peers, [], [], null, JsonSerializer.SerializeToElement(new { })), ContractJson.Options),
                JsonSerializer.SerializeToElement(new PeerHealth(cluster.ClusterId, peer.PeerId, peer.InstanceId, EPeerStatus.Ready, null), ContractJson.Options),
                [new ResourceSample(Guid.NewGuid(), peer.PeerId, now, [new("cpu.usage", "machine", 25, "percent")], true, peer.InstanceId)],
                new ExecutionHistoryPage([new(null, new(execution, "workflow", "revision", EOutcome.Succeeded, now.AddSeconds(-1), now, [], 0, 0))], null), new LogQueryResult([], null, true));
        }).ToArray());
    }

    /// <summary>実APIと同じJSON契約を使います。</summary>
    /// <returns>型付きJSON本文のHTTP結果です。</returns>
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };
}
