using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実ClientHostから選択したCoreだけへ登録削除を送り、保存結果を確認します。</summary>
public sealed class ApplicationRemovalProcessTests
{
    /// <summary>古い版の削除を拒否し、他Peerの同名登録とCore再起動後の状態を維持します。</summary>
    /// <returns>実HTTP・UDPと二つのCoreを通した削除の確認です。</returns>
    [Fact]
    public async Task RemovalThroughClientHostPreservesOtherPeersAndRejectsStaleVersionsAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await ReadyAsync(first, timeout.Token);
        await ReadyAsync(second, timeout.Token);
        await client.StartAsync(timeout.Token);
        using (HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network",
            new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token))
        {
            binding.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(false);
            DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(false);
            return clusters?.Any(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal) && item.Peers.Count == 2) == true;
        }, timeout.Token);
        ProcessDefinition definition = new("player", EProcessMode.Managed, Path.Combine(first.DirectoryPath, "player.exe"), [], first.DirectoryPath, LogicalName: "Player");
        Assert.Equal("succeeded", (await PostAsync(client, cluster, "a", "register", new ProcessRegistrationRequest(definition), timeout.Token)).GetProperty("outcome").GetString());
        Assert.Equal("succeeded", (await PostAsync(client, cluster, "b", "register", new ProcessRegistrationRequest(definition), timeout.Token)).GetProperty("outcome").GetString());
        string old = Assert.Single(await first.GetAsync<JsonElement[]>("processes.list", timeout.Token)).GetProperty("version").GetString()!;
        Assert.Equal("succeeded", (await PostAsync(client, cluster, "a", "register", new ProcessRegistrationRequest(definition with { LogicalName = "Renamed" }, old), timeout.Token)).GetProperty("outcome").GetString());
        string current = Assert.Single(await first.GetAsync<JsonElement[]>("processes.list", timeout.Token)).GetProperty("version").GetString()!;
        Assert.NotEqual("succeeded", (await PostAsync(client, cluster, "a", "remove", new ProcessRemovalRequest(definition.Id, old), timeout.Token)).GetProperty("outcome").GetString());
        Assert.Equal(current, Assert.Single(await first.GetAsync<JsonElement[]>("processes.list", timeout.Token)).GetProperty("version").GetString());
        Assert.Equal("succeeded", (await PostAsync(client, cluster, "a", "remove", new ProcessRemovalRequest(definition.Id, current), timeout.Token)).GetProperty("outcome").GetString());
        Assert.Empty(await first.GetAsync<JsonElement[]>("processes.list", timeout.Token));
        Assert.Equal("player", Assert.Single(await second.GetAsync<JsonElement[]>("processes.list", timeout.Token)).GetProperty("definition").GetProperty("id").GetString());
        await first.StopAsync(timeout.Token);
        await ReadyAsync(first, timeout.Token);
        Assert.Empty(await first.GetAsync<JsonElement[]>("processes.list", timeout.Token));
    }

    /// <summary>Coreの運用準備が完了してから管理操作へ進みます。</summary>
    /// <returns>今回起動したCoreの準備完了です。</returns>
    private static async Task ReadyAsync(CorePeer peer, CancellationToken cancellationToken = default)
    {
        await peer.StartAsync(cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("status").GetString() is "ready"
            && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ブラウザーと同じHTTP入口から、クラスタとPeerを明示して一度だけ送ります。</summary>
    /// <returns>Coreが確定した登録操作の結果です。</returns>
    private static async Task<JsonElement> PostAsync(ClientHostProcess client, string cluster, string peer, string operation, object body, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Post, "api/processes/" + operation, body, cluster, peer, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }
}
