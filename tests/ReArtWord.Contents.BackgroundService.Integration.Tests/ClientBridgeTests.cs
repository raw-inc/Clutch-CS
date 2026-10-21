using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別プロセスの画面HostがCoreへ参加せず管理と元結果照会を行うことを確認します。</summary>
public sealed class ClientBridgeTests
{
    /// <summary>クライアント終了がCoreを止めず、再起動後も元操作の実体を変更しません。</summary>
    /// <returns>実HTTPローカル画面と実UDP Core管理の結合確認です。</returns>
    [Fact]
    public async Task ClientDoesNotJoinOrOwnCoreAndPreservesOriginalCommandAcrossRestarts()
    {
        string clusterId = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(clusterId, "a", port, true);
        ClientHostProcess client = new(Path.Combine(core.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = ((IAsyncDisposable)core).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        await core.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await core.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using (HttpResponseMessage missingTarget = await client.SendAsync(HttpMethod.Get, "api/status").ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingTarget.StatusCode);
        }
        using (HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(core.InterfaceId, "127.0.0.1", "127.255.255.255", port)).ConfigureAwait(true))
        {
            binding.EnsureSuccessStatusCode();
        }
        await DiscoveredAsync(client, clusterId).ConfigureAwait(true);
        JsonElement health = await core.GetAsync<JsonElement>("health").ConfigureAwait(true);
        Guid originalInstance = health.GetProperty("instanceId").GetGuid();
        using (HttpResponseMessage statusResponse = await client.SendAsync(HttpMethod.Get, "api/status", clusterId: clusterId, peerId: "a").ConfigureAwait(true))
        {
            statusResponse.EnsureSuccessStatusCode();
            JsonElement status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
            Assert.Equal(1, status.GetProperty("peers").GetArrayLength());
        }
        Guid requestId = Guid.NewGuid();
        object secret = new { name = "fixture-only", value = "not-a-real-credential" };
        Guid commandId;
        using (HttpResponseMessage saved = await client.SendAsync(HttpMethod.Put, "api/secrets", secret, clusterId, "a", requestId).ConfigureAwait(true))
        {
            saved.EnsureSuccessStatusCode();
            commandId = Guid.Parse(Assert.Single(saved.Headers.GetValues("X-Contents-Command")));
        }
        LogQueryResult? audit = null;
        await CorePeer.WaitAsync(async token =>
        {
            audit = await core.GetAsync<LogQueryResult>("logs.query", new LogQuery(), token).ConfigureAwait(true);
            return audit.Entries.Any(entry => entry.Data?.Json.TryGetProperty("operation", out JsonElement operation) == true && string.Equals(operation.GetString(), "secrets.set", StringComparison.Ordinal));
        }).ConfigureAwait(true);
        WorkflowLogEntry changed = Assert.Single(audit!.Entries, entry => entry.Data?.Json.TryGetProperty("operation", out JsonElement operation) == true && string.Equals(operation.GetString(), "secrets.set", StringComparison.Ordinal));
        Assert.Equal("fixture-only", changed.Data!.Json.GetProperty("target").GetString());
        Assert.Equal(Dns.GetHostName(), changed.Data.Json.GetProperty("originMachine").GetString());
        Assert.DoesNotContain("not-a-real-credential", JsonSerializer.Serialize(changed, ContractJson.Options), StringComparison.Ordinal);
        await client.StopAsync().ConfigureAwait(true);
        Assert.Equal(originalInstance, (await core.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
        await core.StopAsync().ConfigureAwait(true);
        await core.StartAsync().ConfigureAwait(true);
        Guid newInstance = (await core.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("instanceId").GetGuid();
        Assert.NotEqual(originalInstance, newInstance);
        await client.StartAsync().ConfigureAwait(true);
        await DiscoveredAsync(client, clusterId).ConfigureAwait(true);
        using (HttpResponseMessage repeated = await client.SendAsync(HttpMethod.Put, "api/secrets", secret, clusterId, "a", requestId).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        }
        using HttpResponseMessage receipt = await client.SendAsync(HttpMethod.Get, "client/commands/" + commandId.ToString("D") + "/result").ConfigureAwait(true);
        receipt.EnsureSuccessStatusCode();
        ManagementQueryResult? result = await receipt.Content.ReadFromJsonAsync<ManagementQueryResult>(ContractJson.Options).ConfigureAwait(true);
        Assert.Equal(Contracts.Execution.EOutcome.Succeeded, result?.Reply?.Outcome);
        Assert.Equal(originalInstance, (await ReadRecordAsync(client, commandId).ConfigureAwait(true)).GetProperty("targetInstanceId").GetGuid());
    }

    /// <summary>受動発見の完了を実受信情報で確認します。</summary>
    /// <returns>指定クラスタの観測です。</returns>
    private static Task DiscoveredAsync(ClientHostProcess client, string clusterId, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true);
        return clusters?.Any(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal) && cluster.Peers.Count == 1) == true;
    }, cancellationToken);

    /// <summary>要求本文なしで記録した元の実体を参照します。</summary>
    /// <returns>元の管理記録です。</returns>
    private static async Task<JsonElement> ReadRecordAsync(ClientHostProcess client, Guid id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/commands/" + id.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(true);
    }
}
