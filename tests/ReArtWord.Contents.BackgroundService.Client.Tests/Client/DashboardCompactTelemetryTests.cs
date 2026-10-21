using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>概要画面が配信方式に依存せず、同じ履歴と差分位置を維持することを確認します。</summary>
public sealed class DashboardCompactTelemetryTests
{
    /// <summary>新旧Hostの応答を単一・全クラスタ表示で読み、次回には受信済み位置を返します。</summary>
    /// <returns>値・時刻・欠測・実体と差分統合の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FullAndCompactResponsesKeepTheSameHistoryAndCursor(bool compact, bool allClusters)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 1, now, now);
        ResourceSample[] samples = Enumerable.Range(0, 5).Select(index => new ResourceSample(Guid.NewGuid(), peer.PeerId, now.AddSeconds(index - 5),
            [new("cpu.usage", "machine", index, "%"), new("disk.read.rate", "machine", null, "bytes/s", "initial")], InstanceId: peer.InstanceId)).ToArray();
        int reads = 0;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string discovery = "/clusters";
            if (request.RequestUri!.AbsolutePath.EndsWith(discovery, StringComparison.Ordinal))
            {
                return Json(new DiscoveredCluster[] { new("cluster", [peer], [], now) });
            }
            Assert.Contains("compact=true", request.RequestUri.Query, StringComparison.Ordinal);
            Guid? cursor = null;
            if (allClusters)
            {
                DashboardSnapshotQuery query = Assert.Single((await request.Content!.ReadFromJsonAsync<DashboardSnapshotQuery[]>(ContractJson.Options, token).ConfigureAwait(false))!);
                cursor = query.After;
            }
            else
            {
                if (reads != 0)
                {
                    Assert.Contains("after=" + samples[2].Id.ToString("D"), request.RequestUri.Query, StringComparison.Ordinal);
                    cursor = samples[2].Id;
                }
            }
            ResourceSample[] incoming = samples.Take(3).ToArray();
            if (reads++ != 0)
            {
                Assert.Equal(samples[2].Id, cursor);
                incoming = samples.Skip(3).ToArray();
            }
            DashboardPeerSnapshot snapshot = new("cluster", peer, now, null, null, incoming);
            if (compact)
            {
                snapshot = snapshot with { Samples = [], SampleSeries = DashboardSampleSeries.Encode(incoming) };
            }
            if (allClusters)
            {
                return Json(new[] { snapshot });
            }
            return Json(snapshot);
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(http);
        DashboardTelemetry selected = new(api);
        AllClustersTelemetry overview = new(api);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            if (allClusters)
            {
                await overview.RefreshAsync();
                selected = overview.Observations["cluster"];
            }
            else
            {
                await selected.RefreshTargetAsync("cluster", "peer", peer.InstanceId, [peer]);
            }
        }
        IReadOnlyList<ResourceSample> restored = Assert.Single(selected.Peers).Samples;
        Assert.Equal(2, reads);
        Assert.Equal(JsonSerializer.Serialize(samples, ContractJson.Options), JsonSerializer.Serialize(restored, ContractJson.Options));
    }

    /// <summary>HTTP境界で実際のJSON契約を往復させます。</summary>
    /// <returns>呼出元が破棄する応答です。</returns>
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
}
