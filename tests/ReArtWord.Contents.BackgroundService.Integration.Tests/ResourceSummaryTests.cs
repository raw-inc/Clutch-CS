using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実CoreのUDP応答が、概要と詳細の用途を送信前に分けることを検証します。</summary>
public sealed class ResourceSummaryTests
{
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
