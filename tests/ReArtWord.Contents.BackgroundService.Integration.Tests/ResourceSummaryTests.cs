using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実CoreのUDP応答が、概要と詳細の用途を送信前に分けることを検証します。</summary>
public sealed class ResourceSummaryTests
{
    /// <summary>CoreとClientHostの実プロセスを通し、計測・稼働確認・差分取得を照合します。</summary>
    /// <returns>隔離したUDP観測とHTTP配信の確認です。</returns>
    [Fact(Skip = "旧仕様: 再配信した標本の換算日時が完全一致する前提です。現在は元の経過時間を表示先の時計へ換算します。現行の実配信はClientHostRelaysSampleIdentityAcrossObservationClockMappingAsync、期限維持はDashboardObservationAgeTestsで検証します。")]
    public async Task ClientHostRelaysTheSameSampleAndRetainsItsFirstObservation()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer peer = new(cluster, "sample-peer", port, true);
        await using ConfiguredAsyncDisposable coreLifetime = peer.ConfigureAwait(true);
        ClientHostProcess client = new(Path.Combine(peer.DirectoryPath, "client"));
        await using ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network",
            new ClientNetworkBinding(peer.InterfaceId, "127.0.0.1", "127.255.255.255", port)).ConfigureAwait(true);
        binding.EnsureSuccessStatusCode();
        DashboardPeerSnapshot? observed = null;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/dashboard", clusterId: cluster, peerId: "sample-peer", cancellationToken: token).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return false;
            }
            observed = await response.Content.ReadFromJsonAsync<DashboardPeerSnapshot>(ClientObservationJson.Options, token).ConfigureAwait(true);
            return observed?.Health is not null && observed.Samples.Count != 0;
        }).ConfigureAwait(true);
        DashboardPeerSnapshot first = Assert.IsType<DashboardPeerSnapshot>(observed);
        ResourceSample sample = first.Samples[^1];
        Assert.Equal(first.Peer.InstanceId, sample.InstanceId);
        Assert.Equal("sample-peer", sample.PeerId);
        Assert.True(sample.ObservedAt <= first.CheckedAt);
        using HttpResponseMessage repeated = await client.SendAsync(HttpMethod.Get, "client/dashboard", clusterId: cluster, peerId: "sample-peer").ConfigureAwait(true);
        repeated.EnsureSuccessStatusCode();
        DashboardPeerSnapshot second = (await repeated.Content.ReadFromJsonAsync<DashboardPeerSnapshot>(ClientObservationJson.Options).ConfigureAwait(true))!;
        Assert.Equal(sample.ObservedAt, second.Samples.Single(item => item.Id == sample.Id).ObservedAt);
        using HttpResponseMessage delta = await client.SendAsync(HttpMethod.Get, "client/dashboard?after=" + sample.Id, clusterId: cluster, peerId: "sample-peer").ConfigureAwait(true);
        delta.EnsureSuccessStatusCode();
        DashboardPeerSnapshot difference = (await delta.Content.ReadFromJsonAsync<DashboardPeerSnapshot>(ClientObservationJson.Options).ConfigureAwait(true))!;
        Assert.DoesNotContain(difference.Samples, item => item.Id == sample.Id);
        Assert.NotNull(difference.Health);
    }

    /// <summary>表示日時の換算後も、実Coreの同じ採取結果と差分位置を維持します。</summary>
    /// <returns>隔離したUDP観測とHTTP配信の確認です。</returns>
    [Fact]
    public async Task ClientHostRelaysSampleIdentityAcrossObservationClockMappingAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer peer = new(cluster, "sample-peer", port, true);
        await using ConfiguredAsyncDisposable coreLifetime = peer.ConfigureAwait(true);
        ClientHostProcess client = new(Path.Combine(peer.DirectoryPath, "client"));
        await using ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network",
            new ClientNetworkBinding(peer.InterfaceId, "127.0.0.1", "127.255.255.255", port)).ConfigureAwait(true);
        binding.EnsureSuccessStatusCode();
        DashboardPeerSnapshot? observed = null;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/dashboard", clusterId: cluster, peerId: "sample-peer", cancellationToken: token).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return false;
            }
            observed = await response.Content.ReadFromJsonAsync<DashboardPeerSnapshot>(ClientObservationJson.Options, token).ConfigureAwait(true);
            return observed?.Health is not null && observed.Samples.Count != 0;
        }).ConfigureAwait(true);
        DashboardPeerSnapshot first = Assert.IsType<DashboardPeerSnapshot>(observed);
        ResourceSample sample = first.Samples[^1];
        Assert.Equal(first.Peer.InstanceId, sample.InstanceId);
        Assert.Equal("sample-peer", sample.PeerId);
        Assert.True(sample.ObservedAt <= first.CheckedAt);
        using HttpResponseMessage repeated = await client.SendAsync(HttpMethod.Get, "client/dashboard", clusterId: cluster, peerId: "sample-peer").ConfigureAwait(true);
        repeated.EnsureSuccessStatusCode();
        DashboardPeerSnapshot second = (await repeated.Content.ReadFromJsonAsync<DashboardPeerSnapshot>(ClientObservationJson.Options).ConfigureAwait(true))!;
        ResourceSample same = second.Samples.Single(item => item.Id == sample.Id);
        Assert.Equal(sample.InstanceId, same.InstanceId);
        Assert.Equal(sample.Metrics, same.Metrics);
        Assert.True(same.ObservedAt <= second.CheckedAt);
        using HttpResponseMessage delta = await client.SendAsync(HttpMethod.Get, "client/dashboard?after=" + sample.Id, clusterId: cluster, peerId: "sample-peer").ConfigureAwait(true);
        delta.EnsureSuccessStatusCode();
        DashboardPeerSnapshot difference = (await delta.Content.ReadFromJsonAsync<DashboardPeerSnapshot>(ClientObservationJson.Options).ConfigureAwait(true))!;
        Assert.DoesNotContain(difference.Samples, item => item.Id == sample.Id);
        Assert.NotNull(difference.Health);
    }

    /// <summary>概要の識別子と必要計測を保持し、従来の詳細要求も維持します。</summary>
    /// <returns>実管理通信の概要と全詳細の検証です。</returns>
    [Fact]
    public async Task LatestResourceQueryProjectsSummaryBeforeTransportAndPreservesFullDetails()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "sample-peer", CorePeer.AvailablePort(), true);
        await using ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)peer).ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        ResourceSample? summary = null;
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement reply = await peer.GetAsync<JsonElement>("resources.latest", new ResourceSampleQuery(Summary: true), token).ConfigureAwait(true);
            if (reply.ValueKind == JsonValueKind.Null)
            {
                return false;
            }
            summary = reply.Deserialize<ResourceSample>(ContractJson.Options);
            return 0 < summary?.Metrics.Count;
        }).ConfigureAwait(true);
        ResourceSample full = await peer.GetAsync<ResourceSample>("resources.latest").ConfigureAwait(true);
        Assert.Equal(full.PeerId, summary!.PeerId);
        Assert.Equal(full.InstanceId, summary.InstanceId);
        Assert.Equal(DashboardSampleHistory.Project(summary).Metrics, summary.Metrics);
        Assert.Contains(summary.Metrics, metric => string.Equals(metric.Name, "cpu.usage", StringComparison.Ordinal));
        Assert.True(summary.Metrics.Count < full.Metrics.Count);
        JsonElement current = await peer.GetAsync<JsonElement>("dashboard.current").ConfigureAwait(true);
        ResourceSample bundled = current.GetProperty("resource").Deserialize<ResourceSample>(ContractJson.Options)!;
        Assert.True(bundled.ObservedAt <= current.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(full.InstanceId, current.GetProperty("health").GetProperty("instanceId").GetGuid());
        Assert.Equal(full.InstanceId, current.GetProperty("status").GetProperty("instanceId").GetGuid());
        Assert.Equal(full.PeerId, bundled.PeerId);
        Assert.Equal(full.InstanceId, bundled.InstanceId);
        Assert.Equal(DashboardSampleHistory.Project(bundled).Metrics, bundled.Metrics);
        Assert.Contains(bundled.Metrics, metric => string.Equals(metric.Name, "cpu.usage", StringComparison.Ordinal));
        string[] rates = ["disk.read.rate", "disk.write.rate", "network.sent.rate", "network.received.rate"];
        foreach (string rate in rates)
        {
            ResourceMetric metric = Assert.Single(summary.Metrics, metric => string.Equals(metric.Name, rate, StringComparison.Ordinal));
            Assert.Equal("machine", metric.Target);
            Assert.Equal("bytes/s", metric.Unit);
            Assert.Contains(bundled.Metrics, metric => string.Equals(metric.Name, rate, StringComparison.Ordinal));
            Assert.Contains(full.Metrics, metric => string.Equals(metric.Name, rate, StringComparison.Ordinal));
        }
    }
}
