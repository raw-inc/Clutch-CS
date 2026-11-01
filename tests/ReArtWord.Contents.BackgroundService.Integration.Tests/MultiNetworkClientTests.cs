using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>独立クラスタの実Core二つを、一ClientHostの複数UDP割当から管理します。</summary>
public sealed class MultiNetworkClientTests
{
    /// <summary>同名Peer・ワークフローを誤配送せず、保存操作記録と再起動後の監視復元を確認します。</summary>
    /// <returns>実HTTP・UDP・プロセス境界の確認です。</returns>
    [Fact]
    public async Task OneClientRoutesReadsWritesAndReceiptsAcrossIndependentNetworksAsync()
    {
        string firstId = Guid.NewGuid().ToString("N");
        string secondId = Guid.NewGuid().ToString("N");
        int[] ports = CorePeer.AvailableUdpPorts(2);
        int firstPort = ports[0], secondPort = ports[1];
        CorePeer first = new(firstId, "same-peer", firstPort, true);
        CorePeer second = new(secondId, "same-peer", secondPort, true);
        await using ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        ClientNetworkBinding[] bindings = [new(first.InterfaceId, "127.0.0.1", "127.255.255.255", firstPort), new(second.InterfaceId, "127.0.0.1", "127.255.255.255", secondPort)];
        using (HttpResponseMessage selected = await client.SendAsync(HttpMethod.Put, "/client/networks", bindings).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        }
        await WaitForClustersAsync(client, 2).ConfigureAwait(true);
        foreach (string clusterId in new[] { firstId, secondId })
        {
            using HttpResponseMessage healthResponse = await client.SendAsync(HttpMethod.Get, "/health", clusterId: clusterId, peerId: "same-peer").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            JsonElement health = await healthResponse.Content.ReadFromJsonAsync<JsonElement>(ContractJson.Options).ConfigureAwait(true);
            Assert.Equal(clusterId, health.GetProperty("clusterId").GetString());
            Assert.True(health.GetProperty("ready").GetBoolean());
            WorkflowDefinition definition = new("same-workflow", clusterId, [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(clusterId) })], []);
            WorkflowSaveRequest request = new(Guid.NewGuid(), definition, null);
            using HttpResponseMessage saved = await client.SendAsync(HttpMethod.Post, "/api/workflows/save", request, clusterId, "same-peer", request.OperationId).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            WorkflowSaveResult? result = await saved.Content.ReadFromJsonAsync<WorkflowSaveResult>(ContractJson.Options).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result!.Saved.Outcome);
            string commandId = Assert.Single(saved.Headers.GetValues("X-Contents-Command"));
            using HttpResponseMessage receipt = await client.SendAsync(HttpMethod.Get, "/client/commands/" + commandId + "/result").ConfigureAwait(true);
            ManagementQueryResult? confirmed = await receipt.Content.ReadFromJsonAsync<ManagementQueryResult>(ContractJson.Options).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, confirmed!.Reply!.Outcome);
        }
        JsonElement firstSaved = await first.GetAsync<JsonElement>("workflows.read", "same-workflow").ConfigureAwait(true);
        JsonElement secondSaved = await second.GetAsync<JsonElement>("workflows.read", "same-workflow").ConfigureAwait(true);
        Assert.Equal(firstId, firstSaved.GetProperty("saved").GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal(secondId, secondSaved.GetProperty("saved").GetProperty("definition").GetProperty("name").GetString());
        await client.StopAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        await WaitForClustersAsync(client, 2).ConfigureAwait(true);
        using (HttpResponseMessage removed = await client.SendAsync(HttpMethod.Put, "/client/networks", new[] { bindings[0] }).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }
        await WaitForClustersAsync(client, 1).ConfigureAwait(true);
        using HttpResponseMessage unavailable = await client.SendAsync(HttpMethod.Get, "/health", clusterId: secondId, peerId: "same-peer").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        using HttpResponseMessage remaining = await client.SendAsync(HttpMethod.Get, "/health", clusterId: firstId, peerId: "same-peer").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, remaining.StatusCode);
    }

    /// <summary>存在しないクラスタを成功扱いせず、実受信数を待ちます。</summary>
    /// <returns>期限付きのPeer発見待ちです。</returns>
    private static Task WaitForClustersAsync(ClientHostProcess client, int count) => CorePeer.WaitAsync(async token =>
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "/client/clusters", cancellationToken: token).ConfigureAwait(false);
        DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(false);
        return clusters?.Length == count;
    });
}
