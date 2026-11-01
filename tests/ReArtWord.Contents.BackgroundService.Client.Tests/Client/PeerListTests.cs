using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ピア一覧が選択・別実体・過去値を混同せず、必要な稼働確認だけを取得します。</summary>
public sealed class PeerListTests
{
    /// <summary>同名Peerをクラスタ別に確認し、再起動や欠測で古い版を再利用しません。</summary>
    /// <returns>全体一覧の対象同一性と失敗表示の確認です。</returns>
    [Fact]
    public async Task VersionsBelongToTheMatchingInstanceAndReadsDoNotChangeSelection()
    {
        EditorTestClock clock = new();
        DiscoveredCluster[] clusters = [Cluster("a", clock.GetUtcNow()), Cluster("b", clock.GetUtcNow())];
        bool mismatch = false, failure = false, discoveryFailed = false;
        List<string> paths = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            string discovery = "/client/clusters";
            if (string.Equals(path, discovery, StringComparison.Ordinal))
            {
                if (discoveryFailed)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return Reply(clusters);
            }
            Assert.Equal("/client/dashboard/batch", path);
            DashboardSnapshotQuery[] queries = (await request.Content!.ReadFromJsonAsync<DashboardSnapshotQuery[]>(ContractJson.Options, token).ConfigureAwait(false))!;
            Assert.All(queries, query => Assert.True(query.HealthOnly));
            if (failure)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return Reply(queries.Select(query =>
            {
                PeerInfo peer = clusters.Single(cluster => string.Equals(cluster.ClusterId, query.ClusterId, StringComparison.Ordinal)).Peers[0];
                Guid instance = peer.InstanceId;
                if (mismatch)
                {
                    instance = Guid.NewGuid();
                }
                return new DashboardPeerSnapshot(query.ClusterId, peer, clock.GetUtcNow(), null,
                    Health(query.ClusterId, peer.PeerId, instance, "1.0.0+" + query.ClusterId), []);
            }).ToArray());
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client, new(clock)) { SelectedClusterId = "selected", SelectedPeerId = "untouched" };
        PeerListTelemetry list = new(api);
        await list.RefreshAsync();
        Assert.Equal(2, list.Total);
        Assert.Equal(clusters.Select(cluster => "1.0.0+" + cluster.ClusterId), list.Rows.Select(row => row.Health!.BuildVersion));
        Assert.Equal("selected", api.SelectedClusterId);
        Assert.Equal("untouched", api.SelectedPeerId);
        mismatch = true;
        await list.RefreshAsync();
        Assert.All(list.Rows, row => Assert.Null(row.Health));
        mismatch = false;
        failure = true;
        await list.RefreshAsync();
        Assert.All(list.Rows, row => Assert.Null(row.Health));
        failure = false;
        await list.RefreshAsync();
        Assert.All(list.Rows, row => Assert.NotNull(row.Health));
        discoveryFailed = true;
        await list.RefreshAsync();
        Assert.NotNull(list.Problem);
        Assert.Equal(2, list.Rows.Count);
        Assert.All(list.Rows, row => Assert.Null(row.Health));
        Assert.Equal(5, paths.Count(path => string.Equals(path, "/client/clusters", StringComparison.Ordinal)));
    }

    /// <summary>一覧は128件に制限し、次ページでも履歴・ログを取得しません。</summary>
    /// <returns>全クラスタを跨ぐ有限取得範囲です。</returns>
    [Fact]
    public async Task PagingBoundsHealthReadsAcrossAllClusters()
    {
        DiscoveredCluster[] clusters = Enumerable.Range(0, 129).Select(index => Cluster(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        int healthReads = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string discovery = "/client/clusters";
            if (string.Equals(request.RequestUri!.AbsolutePath, discovery, StringComparison.Ordinal))
            {
                return Reply(clusters);
            }
            Assert.Equal("/client/dashboard/batch", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref healthReads);
            DashboardSnapshotQuery[] queries = (await request.Content!.ReadFromJsonAsync<DashboardSnapshotQuery[]>(ContractJson.Options, token).ConfigureAwait(false))!;
            Assert.InRange(queries.Length, 1, 128);
            return Reply(queries.Select(query =>
            {
                DiscoveredCluster cluster = clusters.Single(item => string.Equals(item.ClusterId, query.ClusterId, StringComparison.Ordinal));
                return new DashboardPeerSnapshot(cluster.ClusterId, cluster.Peers[0], DateTimeOffset.UtcNow, null,
                    Health(cluster.ClusterId, "same", cluster.Peers[0].InstanceId, "version"), []);
            }).ToArray());
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        PeerListTelemetry list = new(new(client));
        await list.RefreshAsync();
        Assert.Equal(1, healthReads);
        Assert.Equal(128, list.Rows.Count);
        list.NextPage();
        await list.RefreshAsync();
        Assert.Equal(2, healthReads);
        Assert.Equal("128", Assert.Single(list.Rows).Cluster);
        list.NextPage();
        Assert.Equal(0, list.Offset);
    }

    /// <summary>外部回線の遅延と一時間の端末時計差があっても、一括照会で全実体を確認します。</summary>
    /// <returns>新旧応答を混在させない一巡の取得です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task RemoteRoundTripAndClockSkewDoNotLoseHealthyPeers(int serverOffsetSeconds)
    {
        DateTimeOffset server = DateTimeOffset.UtcNow.AddSeconds(serverOffsetSeconds);
        Guid host = Guid.NewGuid();
        DiscoveredCluster[] clusters = Enumerable.Range(0, 27).Select(index => Cluster(index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Select(cluster => cluster with { LastSeen = server, Peers = cluster.Peers.Select(peer => peer with { LastSeen = server }).ToArray() }).ToArray();
        int requests = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(200, token).ConfigureAwait(false);
            string discovery = "/client/clusters";
            HttpResponseMessage response;
            if (string.Equals(request.RequestUri!.AbsolutePath, discovery, StringComparison.Ordinal))
            {
                response = Reply(clusters);
            }
            else
            {
                DashboardSnapshotQuery[] queries = (await request.Content!.ReadFromJsonAsync<DashboardSnapshotQuery[]>(ContractJson.Options, token).ConfigureAwait(false))!;
                response = Reply(queries.Select(query =>
                {
                    PeerInfo peer = clusters.Single(cluster => string.Equals(cluster.ClusterId, query.ClusterId, StringComparison.Ordinal)).Peers[0];
                    return new DashboardPeerSnapshot(query.ClusterId, peer, server, null,
                        Health(query.ClusterId, peer.PeerId, peer.InstanceId, "version"), []);
                }).ToArray());
            }
            response.Headers.Add("X-Contents-Observed-At", server.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.Add("X-Contents-Host-Instance", host.ToString("D"));
            return response;
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://dashboard.example/") };
        PeerListTelemetry list = new(new(client));
        await list.RefreshAsync();
        Assert.Equal(2, requests);
        Assert.Equal(27, list.Rows.Count);
        Assert.All(list.Rows, row => Assert.Equal(EPeerStatus.Ready, row.Health!.Status));
    }

    /// <summary>画面の部分モデルではなくCoreのhealth応答と同じ追加項目を含めます。</summary>
    /// <returns>一括取得で埋め込まれる実応答の形です。</returns>
    private static JsonElement Health(string clusterId, string peerId, Guid instanceId, string buildVersion)
        => JsonSerializer.SerializeToElement(new
        {
            status = EPeerStatus.Ready,
            clusterId,
            ready = true,
            participating = true,
            peerId,
            instanceId,
            processId = 1234,
            processStartedAt = DateTimeOffset.UtcNow,
            buildVersion,
            startupProblem = (string?)null,
            synchronizationProblem = (string?)null,
            maintenance = (object?)null,
        }, ContractJson.Options);

    /// <summary>一つの同名Peerを含む新しい発見情報です。</summary>
    /// <returns>クラスタで独立した試験用の構成です。</returns>
    private static DiscoveredCluster Cluster(string id, DateTimeOffset? observed = null)
    {
        DateTimeOffset now = observed ?? DateTimeOffset.UtcNow;
        return new(id, [new("same", Guid.NewGuid(), "pc", "127.0.0.1", 47740, EPeerStatus.Ready, 1, now, now)], ["same"], now);
    }

    /// <summary>実際の管理HTTP形式で返します。</summary>
    /// <returns>呼出元が破棄する応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
}
