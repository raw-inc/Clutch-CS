using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別のClientHostが所有する更新進捗を、実Core経由で参照します。</summary>
public sealed class FleetVisibilityProcessTests
{
    /// <summary>補助Hostだけの保存が通常画面へ届き、実更新を開始せず完了表示まで追従します。</summary>
    /// <returns>実UDPとHTTPを介した進捗観測です。</returns>
    [Fact]
    public async Task SeparateClientHostsObserveProgressThroughCoreWithoutDispatchingUpdatesAsync()
    {
        string cluster = "visibility-" + Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "core", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = ((IAsyncDisposable)core).ConfigureAwait(true);
        string ownerDirectory = Path.Combine(core.DirectoryPath, "owner");
        ClientHostProcess owner = new(ownerDirectory), viewer = new(Path.Combine(core.DirectoryPath, "viewer"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = ((IAsyncDisposable)owner).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable viewerLifetime = ((IAsyncDisposable)viewer).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(100));
        await core.StartAsync(timeout.Token).ConfigureAwait(true);
        await owner.StartAsync(timeout.Token).ConfigureAwait(true);
        await viewer.StartAsync(timeout.Token).ConfigureAwait(true);
        ClientNetworkBinding binding = new(core.InterfaceId, "127.0.0.1", "127.255.255.255", port);
        foreach (ClientHostProcess client in new[] { owner, viewer })
        {
            using HttpResponseMessage configured = await client.SendAsync(HttpMethod.Put, "client/networks", new[] { binding }, cancellationToken: timeout.Token).ConfigureAwait(true);
            configured.EnsureSuccessStatusCode();
        }
        PeerInfo? peer = null;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await owner.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[] discovered = (await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true))!;
            peer = discovered.SelectMany(item => item.Peers).FirstOrDefault(item => item.Status == EPeerStatus.Ready);
            return peer is not null;
        }, timeout.Token).ConfigureAwait(true);
        Guid instance = peer!.InstanceId;
        FleetUpdateTarget target = new(cluster, peer, binding);
        ReleaseManifest manifest = new("visibility-test", RuntimeInformation.RuntimeIdentifier, "app", [new("app", 0, new string('0', 64))], ["old"], "new");
        FleetUpdateRequest request = new(Guid.NewGuid(), new(manifest, ownerDirectory, true), [target]);
        FleetUpdateStatus status = new(request, false, EOutcome.Unknown, [new(target, "Updating", [])], DateTimeOffset.UtcNow);
        string path = Path.Combine(ownerDirectory, "fleet-updates", request.OperationId.ToString("N") + ".json");
        await UpdateProgressFile.WriteAsync(path, status, timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            FleetUpdateOverview overview = await ReadAsync(viewer, token).ConfigureAwait(true);
            return overview.Observed.Any(item => item.Activity.OperationId == request.OperationId && !item.Stale && !item.Activity.Completed);
        }, timeout.Token).ConfigureAwait(true);
        FleetUpdateOverview remote = await ReadAsync(viewer, timeout.Token).ConfigureAwait(true);
        Assert.Empty(remote.Owned);
        FleetUpdateObservation observed = Assert.Single(remote.Observed);
        Assert.Equal(0, observed.Activity.Confirmed);
        Assert.Equal(1, observed.Activity.Total);
        Assert.Equal("Updating", Assert.Single(observed.Activity.Current).State);
        FleetUpdateOverview own = await ReadAsync(owner, timeout.Token).ConfigureAwait(true);
        Assert.Equal(request.OperationId, Assert.Single(own.Owned).OperationId);
        Assert.Empty(own.Observed);
        await UpdateProgressFile.WriteAsync(path, status with { Completed = true, Outcome = EOutcome.Succeeded, Peers = [new(target, "Completed", [])], UpdatedAt = DateTimeOffset.UtcNow }, timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            FleetUpdateOverview overview = await ReadAsync(viewer, token).ConfigureAwait(true);
            return overview.Observed.Any(item => item.Activity.OperationId == request.OperationId && item.Activity.Completed && item.Activity.Confirmed == 1);
        }, timeout.Token).ConfigureAwait(true);
        JsonElement health = await core.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        Assert.Equal(instance, health.GetProperty("instanceId").GetGuid());
        Assert.True(health.GetProperty("ready").GetBoolean());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("maintenance").ValueKind);
        using HttpResponseMessage localHistory = await viewer.SendAsync(HttpMethod.Get, "client/updates", cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Empty((await localHistory.Content.ReadFromJsonAsync<FleetUpdateView[]>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!);
    }

    /// <summary>所有する要求と外部の表示だけを分離したAPIを読みます。</summary>
    /// <returns>現在の表示用スナップショットです。</returns>
    private static async Task<FleetUpdateOverview> ReadAsync(ClientHostProcess client, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/updates/visible", cancellationToken: cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FleetUpdateOverview>(ContractJson.Options, cancellationToken).ConfigureAwait(true))!;
    }
}
