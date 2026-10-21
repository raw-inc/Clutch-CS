using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実CoreとClientHostでHost集約・固定対象・永続履歴を検証します。</summary>
public sealed class HostManagerTests
{
    /// <summary>OS設定を変更せず、重複PCの集約と開始前拒否の記録を実通信で確認します。</summary>
    /// <returns>再起動後も同じ要求と結果を維持した確認です。</returns>
    [Theory]
    [InlineData(true, EDeviceAction.Hosts)]
    [InlineData(false, EDeviceAction.Hosts)]
    [InlineData(false, EDeviceAction.VirtualSwitchCreate)]
    [InlineData(false, EDeviceAction.VirtualSwitchDelete)]
    public async Task ReadsHostsAndKeepsRejectedOperationsAcrossRestartAsync(bool sameHost, EDeviceAction action)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a");
        string secondHost = sameHost ? "host-a"
                                     : "host-b";
        CorePeer second = new(cluster, "b", port, false, secondHost);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        await first.StartAsync();
        await second.StartAsync();
        await client.StartAsync();
        using HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port));
        binding.EnsureSuccessStatusCode();
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true);
            return clusters?.Any(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal) && item.Peers.Count == 2) == true;
        });
        using HttpResponseMessage listing = await client.SendAsync(HttpMethod.Get, "client/host-manager/hosts");
        listing.EnsureSuccessStatusCode();
        DeviceHostInfo[] hosts = (await listing.Content.ReadFromJsonAsync<DeviceHostInfo[]>(ContractJson.Options))!;
        int expectedHosts = sameHost ? 1
                                     : 2;
        Assert.Equal(expectedHosts, hosts.Length);
        Assert.All(hosts, host => Assert.Contains(EDeviceArea.Hosts, host.Capabilities!.Areas));
        DeviceHostInfo selected = hosts[0];
        DeviceReadRequest query = new(EDeviceArea.Hosts);
        using HttpResponseMessage read = await client.SendAsync(HttpMethod.Post, "client/host-manager/read", new DeviceTargetRead(selected.Target, query));
        read.EnsureSuccessStatusCode();
        DeviceSnapshot before = (await read.Content.ReadFromJsonAsync<DeviceSnapshot>(ContractJson.Options))!;
        Assert.NotNull(DeviceSnapshots.Read<DeviceHostsState>(before.Data).Content);
        if (sameHost && OperatingSystem.IsWindows())
        {
            using HttpResponseMessage overview = await client.SendAsync(HttpMethod.Post, "client/host-manager/read", new DeviceTargetRead(selected.Target, new(EDeviceArea.Firewall, "overview")));
            overview.EnsureSuccessStatusCode();
            DeviceSnapshot index = (await overview.Content.ReadFromJsonAsync<DeviceSnapshot>(ContractJson.Options))!;
            string[] ids = index.Data.GetProperty("rules").EnumerateArray().Take(2).Select(rule => rule.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(2, ids.Length);
            using HttpResponseMessage rules = await client.SendAsync(HttpMethod.Post, "client/host-manager/read", new DeviceTargetRead(selected.Target,
                new(EDeviceArea.Firewall, "bulk", Filter: System.Text.Json.JsonSerializer.Serialize(ids))));
            rules.EnsureSuccessStatusCode();
            DeviceFirewallTemplates templates = DeviceSnapshots.Read<DeviceFirewallTemplates>((await rules.Content.ReadFromJsonAsync<DeviceSnapshot>(ContractJson.Options))!.Data);
            Assert.Equal(2, templates.Rules.Count);
            Assert.All(templates.Rules, item => Assert.Equal(64, item.Rule.Revision!.Length));
        }
        DeviceChangeRequest rejected = new(Guid.NewGuid(), selected.Target.HostId, selected.Target.InstanceId, EDeviceAction.Hosts,
            query, new string('0', 64), DeviceSnapshots.Create(new DeviceHostsChange("127.0.0.1 localhost\n")).Data);
        if (action == EDeviceAction.VirtualSwitchCreate)
        {
            rejected = rejected with { Action = action, Read = new(EDeviceArea.Network, "switches"), Change = DeviceSnapshots.Create(new DeviceVirtualSwitchChange("RejectedFixture", "Private")).Data, AcknowledgeDisconnect = true };
        }
        if (action == EDeviceAction.VirtualSwitchDelete)
        {
            rejected = rejected with { Action = action, Read = new(EDeviceArea.Network, "switch/" + Guid.NewGuid().ToString("D")), Change = DeviceSnapshots.Create(new DeviceVirtualSwitchChange()).Data, AcknowledgeDisconnect = true };
        }
        DeviceBatchRequest batch = new(Guid.NewGuid(), [new(selected.Target, rejected)]);
        using HttpResponseMessage accepted = await client.SendAsync(HttpMethod.Post, "client/host-manager/batches", batch);
        accepted.EnsureSuccessStatusCode();
        DeviceBatchState? finished = null;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/host-manager/batches/" + batch.OperationId, cancellationToken: token).ConfigureAwait(true);
            finished = await response.Content.ReadFromJsonAsync<DeviceBatchState>(ContractJson.Options, token).ConfigureAwait(true);
            return finished?.Running == false;
        });
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(finished!.Results)!.Outcome);
        using HttpResponseMessage unchanged = await client.SendAsync(HttpMethod.Post, "client/host-manager/read", new DeviceTargetRead(selected.Target, query));
        Assert.Equal(before.Revision, (await unchanged.Content.ReadFromJsonAsync<DeviceSnapshot>(ContractJson.Options))!.Revision);
        using HttpResponseMessage wrongInstance = await client.SendAsync(HttpMethod.Post, "client/host-manager/read", new DeviceTargetRead(selected.Target with { InstanceId = Guid.NewGuid() }, query));
        Assert.False(wrongInstance.IsSuccessStatusCode);
        await client.StopAsync();
        await client.StartAsync();
        using HttpResponseMessage duplicate = await client.SendAsync(HttpMethod.Post, "client/host-manager/batches", batch);
        duplicate.EnsureSuccessStatusCode();
        DeviceBatchState restored = (await duplicate.Content.ReadFromJsonAsync<DeviceBatchState>(ContractJson.Options))!;
        Assert.False(restored.Running);
        Assert.Equal(ContractJson.Hash(finished), ContractJson.Hash(restored));
        await client.StopAsync();
        DeviceBatchState lostResponse = restored with { Results = [new(rejected.OperationId, EOutcome.Unknown, "Fixture.ResponseLost")] };
        string savedPath = Path.Combine(first.DirectoryPath, "client", "host-manager", batch.OperationId.ToString("N") + ".json");
        await File.WriteAllTextAsync(savedPath, System.Text.Json.JsonSerializer.Serialize(lostResponse, ContractJson.Options));
        await client.StartAsync();
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage discovery = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[]? current = await discovery.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true);
            return current?.SelectMany(item => item.Peers).Any(peer => peer.InstanceId == selected.Target.InstanceId) == true;
        });
        using HttpResponseMessage recovered = await client.SendAsync(HttpMethod.Post, "client/host-manager/batches/" + batch.OperationId + "/reconcile", new { });
        recovered.EnsureSuccessStatusCode();
        DeviceBatchState queried = (await recovered.Content.ReadFromJsonAsync<DeviceBatchState>(ContractJson.Options))!;
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(queried.Results)!.Outcome);
        Assert.Equal(finished.Results[0]!.Reason, queried.Results[0]!.Reason);
    }
}
