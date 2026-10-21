using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>ファイル画面の変更要求を、実ClientHostと二つのCoreの通信境界で検証します。</summary>
public sealed class FilePaneActionTests
{
    /// <summary>作成・削除の結果が一覧へ反映され、競合と古い実体を拒否します。</summary>
    /// <returns>隔離ファイルと実HTTP・Peer通信による検証です。</returns>
    [Fact]
    public async Task CreatesAndDeletesThroughThePinnedHostWithoutOverwritingOrReplayingAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a"), second = new(cluster, "b", port, false, "host-b");
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        string directory = Path.Combine(second.DirectoryPath, "content"), otherDirectory = Path.Combine(first.DirectoryPath, "content");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(otherDirectory);
        string keep = Path.Combine(directory, "keep.txt"), other = Path.Combine(otherDirectory, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep").ConfigureAwait(true);
        await File.WriteAllTextAsync(other, "other host").ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage network = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port)).ConfigureAwait(true);
        network.EnsureSuccessStatusCode();
        PeerInfo? target = null;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[] clusters = (await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true))!;
            PeerInfo[] peers = clusters.SelectMany(item => item.Peers).ToArray();
            target = peers.FirstOrDefault(peer => string.Equals(peer.PeerId, "b", StringComparison.Ordinal));
            return peers.Length == 2 && peers.All(peer => peer.Status is EPeerStatus.Ready) && target is not null;
        }).ConfigureAwait(true);
        using HttpClient http = new() { BaseAddress = client.Address, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("X-Contents-Cluster", cluster);
        http.DefaultRequestHeaders.Add("X-Contents-Peer", target!.PeerId);
        http.DefaultRequestHeaders.Add("X-Contents-Instance", target.InstanceId.ToString("D"));
        string folder = Path.Combine(directory, "日本語 folder");
        RemoteExecutionRequest create = Request(target, "directory.create", folder);
        NodeResult created = await ExecuteAsync(http, create).ConfigureAwait(true);
        Assert.True(created.Outcome is EOutcome.Succeeded, created.ReasonCode + ": " + created.Message);
        Assert.True(Directory.Exists(folder));
        FileBrowseResult listing = await BrowseAsync(http, directory).ConfigureAwait(true);
        Assert.Contains(listing.Entries, entry => entry.IsDirectory && string.Equals(entry.Name, "日本語 folder", StringComparison.Ordinal));
        Assert.NotEqual(EOutcome.Succeeded, (await ExecuteAsync(http, Request(target, "directory.create", folder)).ConfigureAwait(true)).Outcome);
        Assert.NotEqual(EOutcome.Succeeded, (await ExecuteAsync(http, Request(target, "directory.create", keep)).ConfigureAwait(true)).Outcome);
        await File.WriteAllTextAsync(Path.Combine(folder, "nested.txt"), "delete with folder").ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, (await ExecuteAsync(http, Request(target, "file.delete", folder)).ConfigureAwait(true)).Outcome);
        Assert.NotEqual(EOutcome.Succeeded, (await ExecuteAsync(http, Request(target, "directory.delete", keep)).ConfigureAwait(true)).Outcome);
        Assert.Equal("keep", await File.ReadAllTextAsync(keep).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await ExecuteAsync(http, Request(target, "directory.delete", folder)).ConfigureAwait(true)).Outcome);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(EOutcome.Succeeded, (await ExecuteAsync(http, create).ConfigureAwait(true)).Outcome);
        Assert.False(Directory.Exists(folder));
        RemoteExecutionRequest stale = Request(target, "file.delete", keep) with { TargetInstanceId = Guid.NewGuid() };
        Assert.NotEqual(EOutcome.Succeeded, (await ExecuteAsync(http, stale).ConfigureAwait(true)).Outcome);
        Assert.True(File.Exists(keep));
        Assert.Equal(EOutcome.Succeeded, (await ExecuteAsync(http, Request(target, "file.delete", keep)).ConfigureAwait(true)).Outcome);
        Assert.False(File.Exists(keep));
        Assert.Equal("other host", await File.ReadAllTextAsync(other).ConfigureAwait(true));
        Assert.Empty((await BrowseAsync(http, directory).ConfigureAwait(true)).Entries);
    }

    /// <summary>画面と同じ設定、期限、固定実体を持つ一回の操作を組み立てます。</summary>
    /// <returns>再送時も同一IDを維持できる実行要求です。</returns>
    private static RemoteExecutionRequest Request(PeerInfo peer, string operation, string path)
    {
        Guid id = Guid.NewGuid();
        Dictionary<string, object> settings = new() { ["path"] = path };
        string directoryCreate = "directory.create", directoryDelete = "directory.delete";
        if (string.Equals(operation, directoryCreate, StringComparison.Ordinal))
        {
            settings.Add("existingFileMode", "error");
        }
        if (string.Equals(operation, directoryDelete, StringComparison.Ordinal))
        {
            settings.Add("recursive", true);
        }
        NodeDefinition node = new(operation, operation, JsonSerializer.SerializeToElement(settings));
        return new(id, id, "$file-transfer", "file-transfer-v1", node, new Dictionary<string, NodeValue>(), peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    /// <summary>HTTP経由で既存ファイルノードを実行し、公開結果を受け取ります。</summary>
    /// <returns>Coreから確認できたノード自身の結果です。</returns>
    private static async Task<NodeResult> ExecuteAsync(HttpClient http, RemoteExecutionRequest request)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "api/nodes/execute");
        message.Headers.Add("X-Contents-Request", Guid.NewGuid().ToString("D"));
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        using HttpResponseMessage response = await http.SendAsync(message).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeResult>(ContractJson.Options).ConfigureAwait(true))!;
    }

    /// <summary>変更後の一覧を画面と同じ閲覧APIから読み取ります。</summary>
    /// <returns>変更後の項目一覧です。</returns>
    private static async Task<FileBrowseResult> BrowseAsync(HttpClient http, string path)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("api/files/browse", new FileBrowseRequest(path, FromDrives: true), ContractJson.Options).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileBrowseResult>(ContractJson.Options).ConfigureAwait(true))!;
    }
}
