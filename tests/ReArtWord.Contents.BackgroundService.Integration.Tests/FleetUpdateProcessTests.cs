using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実ClientHostから二クラスタ・二hostのCoreを既存Updaterで更新します。</summary>
public sealed class FleetUpdateProcessTests
{
    /// <summary>画面APIの固定計画・受付・継続・再読込みから四ピアの新実体確認まで通します。</summary>
    /// <returns>本番とは独立した実UDP・HTTP・プロセス入替の検証です。</returns>
    [Fact]
    public async Task ClientHostUpdatesEveryPeerAcrossClustersAndRetainsTheResults()
    {
        string root = Path.GetFullPath("../../../../../.work/integration/fleet-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        string payload = Path.Combine(root, "release");
        ReleaseManifest old = await RollingUpdatePeer.PackageAsync(payload).ConfigureAwait(true);
        ReleaseManifest release = old with { ReleaseId = "new" };
        List<RollingUpdatePeer> peers = [];
        Dictionary<(string Cluster, string Peer), Guid> original = [];
        ClientHostProcess client = new(Path.Combine(root, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        try
        {
            for (int index = 0; index < 2; index++)
            {
                string cluster = Guid.NewGuid().ToString("N");
                int port = CorePeer.AvailablePort();
                foreach (string host in new[] { "host-a", "host-b" })
                {
                    string first = "host-a";
                    RollingUpdatePeer peer = new(root, cluster, host, port, payload, old, string.Equals(host, first, StringComparison.Ordinal));
                    peers.Add(peer);
                    original[(cluster, host)] = await peer.StartAsync(timeout.Token).ConfigureAwait(true);
                }
            }
            await client.StartAsync(timeout.Token).ConfigureAwait(true);
            using (HttpResponseMessage configured = await client.SendAsync(HttpMethod.Put, "client/networks",
                peers.Select(peer => peer.Binding).Distinct().ToArray(), cancellationToken: timeout.Token).ConfigureAwait(true))
            {
                configured.EnsureSuccessStatusCode();
            }
            FleetUpdateRequest? plan = null;
            FleetReleaseSelection selection = new(release, payload, true, PeerTimeoutMilliseconds: 60000);
            await CorePeer.WaitAsync(async token =>
            {
                using HttpResponseMessage preview = await client.SendAsync(HttpMethod.Post, "client/updates/preview", selection, cancellationToken: token).ConfigureAwait(true);
                if (!preview.IsSuccessStatusCode)
                {
                    return false;
                }
                plan = await preview.Content.ReadFromJsonAsync<FleetUpdateRequest>(ContractJson.Options, token).ConfigureAwait(true);
                return plan?.Targets.Count == 4;
            }, timeout.Token).ConfigureAwait(true);
            Assert.Equal(2, plan!.Release.MaximumParallelism);
            Assert.Equal(2, plan.Targets.Select(target => target.Peer.HostId).Distinct().Count());
            using (HttpResponseMessage accepted = await client.SendAsync(HttpMethod.Post, "client/updates", plan, cancellationToken: timeout.Token).ConfigureAwait(true))
            {
                accepted.EnsureSuccessStatusCode();
            }
            using (HttpResponseMessage duplicate = await client.SendAsync(HttpMethod.Post, "client/updates", plan, cancellationToken: timeout.Token).ConfigureAwait(true))
            {
                duplicate.EnsureSuccessStatusCode();
                Assert.Equal(plan.OperationId, (await duplicate.Content.ReadFromJsonAsync<FleetUpdateStatus>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!.Request.OperationId);
            }
            FleetUpdateStatus? status = null;
            int peak = 0;
            do
            {
                using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/updates/" + plan.OperationId.ToString("D"), cancellationToken: timeout.Token).ConfigureAwait(true);
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(true));
                status = await response.Content.ReadFromJsonAsync<FleetUpdateStatus>(ContractJson.Options, timeout.Token).ConfigureAwait(true);
                FleetUpdatePeerProgress[] active = status!.Peers.Where(peer => peer.State is "Preparing" or "Updating" or "Retrying").ToArray();
                Assert.InRange(active.Length, 0, plan.Release.MaximumParallelism);
                Assert.Equal(active.Length, active.Select(peer => peer.Target.ClusterId).Distinct().Count());
                Assert.Equal(active.Length, active.Select(peer => peer.Target.Peer.HostId).Distinct().Count());
                peak = Math.Max(peak, active.Length);
                if (status?.Completed != true)
                {
                    await Task.Delay(500, timeout.Token).ConfigureAwait(true);
                }
            }
            while (status?.Completed != true);
            Assert.True(status!.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(status, ContractJson.Options));
            Assert.Equal(2, peak);
            Assert.All(status.Peers, peer => Assert.Equal("Completed", peer.State));
            Assert.All(status.Peers, peer => Assert.Single(peer.Attempts));
            using (HttpResponseMessage overview = await client.SendAsync(HttpMethod.Get, "client/updates", cancellationToken: timeout.Token).ConfigureAwait(true))
            {
                FleetUpdateView[] views = (await overview.Content.ReadFromJsonAsync<FleetUpdateView[]>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
                Assert.Equal(plan.OperationId, Assert.Single(views).OperationId);
                Assert.Equal(4, views[0].Peers.Count);
                Assert.Equal(EOutcome.Succeeded, views[0].Outcome);
            }
            foreach (RollingUpdatePeer peer in peers)
            {
                JsonElement health = await peer.HealthAsync(timeout.Token).ConfigureAwait(true);
                Assert.True(health.GetProperty("ready").GetBoolean());
                Assert.NotEqual(original[(peer.Cluster, peer.Peer)], health.GetProperty("instanceId").GetGuid());
                Assert.Equal(JsonValueKind.Null, health.GetProperty("maintenance").ValueKind);
            }
            await client.StopAsync(timeout.Token).ConfigureAwait(true);
            await client.StartAsync(timeout.Token).ConfigureAwait(true);
            using HttpResponseMessage reloaded = await client.SendAsync(HttpMethod.Get, "client/updates/" + plan.OperationId.ToString("D"), cancellationToken: timeout.Token).ConfigureAwait(true);
            FleetUpdateStatus restored = (await reloaded.Content.ReadFromJsonAsync<FleetUpdateStatus>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
            Assert.Equal(EOutcome.Succeeded, restored.Outcome);
            Assert.Equal(status.Peers.SelectMany(peer => peer.Attempts).Select(attempt => attempt.OperationId),
                restored.Peers.SelectMany(peer => peer.Attempts).Select(attempt => attempt.OperationId));
        }
        finally
        {
            foreach (RollingUpdatePeer peer in peers)
            {
                await peer.DisposeAsync().ConfigureAwait(true);
            }
        }
    }
}
