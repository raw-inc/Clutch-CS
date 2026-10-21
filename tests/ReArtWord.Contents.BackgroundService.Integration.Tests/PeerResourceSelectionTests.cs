using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>ピア別ファイル候補と選択ノードを、別Coreプロセスと管理通信で検証します。</summary>
public sealed class PeerResourceSelectionTests
{
    /// <summary>旧Coreのroots限定閲覧と、選択ノードによる転送を記録する旧仕様の試験です。</summary>
    /// <returns>旧仕様における閲覧範囲と実ピア間転送の確認です。</returns>
    [Fact(Skip = "旧仕様: Coreのfiles.rootsだけを一覧表示し、その外の参照を拒否する前提です。現在のCoreはOSアクセス権を使うため適用できません。現行仕様はOperatingSystemAccessBrowsesAndTransfersOutsideConfiguredRootsAsyncで検証します。")]
    public async Task BrowseAndTransferFollowExplicitPeersAcrossProcessesAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        string sourceRoot = Directory.CreateDirectory(Path.Combine(first.DirectoryPath, "content")).FullName;
        string destinationRoot = Directory.CreateDirectory(Path.Combine(second.DirectoryPath, "content")).FullName;
        string sourcePath = Path.Combine(sourceRoot, "source.txt");
        string destination = Path.Combine(destinationRoot, "received.txt");
        await File.WriteAllTextAsync(sourcePath, "first content").ConfigureAwait(true);
        await DiscoverAsync(first, second, client, cluster, port).ConfigureAwait(true);
        FileBrowseResult roots = await BrowseAsync(client, cluster, "b", new()).ConfigureAwait(true);
        Assert.Equal(destinationRoot, Assert.Single(roots.Entries).Path);
        FileBrowseResult drives = await BrowseAsync(client, cluster, "b", new(FromDrives: true)).ConfigureAwait(true);
        Assert.Contains(drives.Entries, entry => string.Equals(entry.Path, Path.GetPathRoot(destinationRoot), StringComparison.OrdinalIgnoreCase));
        FileBrowseResult driveFolder = await BrowseAsync(client, cluster, "b", new(second.DirectoryPath, FromDrives: true)).ConfigureAwait(true);
        Assert.Null(driveFolder.Problem);
        Assert.Contains(driveFolder.Entries, entry => string.Equals(entry.Path, destinationRoot, StringComparison.OrdinalIgnoreCase));

        Assert.Empty((await BrowseAsync(client, cluster, "b", new(sourceRoot)).ConfigureAwait(true)).Entries);
        FileBrowseResult source = await BrowseAsync(client, cluster, "a", new(sourceRoot)).ConfigureAwait(true);
        Assert.Equal(sourcePath, Assert.Single(source.Entries).Path);
        await VerifySelectedTransferAsync(first, sourcePath, destination).ConfigureAwait(true);
    }

    /// <summary>CoreのOSアクセス方式で旧roots外を参照し、明示したピアへの次回転送にも新しい内容を渡します。</summary>
    /// <returns>実ClientHost・二Coreによる通常参照とドライブ参照の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatingSystemAccessBrowsesAndTransfersOutsideConfiguredRootsAsync(bool fromDrives)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);

        // CorePeerのTOMLは各Peerのcontentをrootsに指定します。現在のCoreではその外もOS権限で扱います。
        string sourceRoot = Directory.CreateDirectory(Path.Combine(first.DirectoryPath, "outside-roots")).FullName;
        string destinationRoot = Directory.CreateDirectory(Path.Combine(second.DirectoryPath, "outside-roots")).FullName;
        string sourcePath = Path.Combine(sourceRoot, "source.txt");
        string destination = Path.Combine(destinationRoot, "received.txt");
        await File.WriteAllTextAsync(sourcePath, "first content").ConfigureAwait(true);
        await DiscoverAsync(first, second, client, cluster, port).ConfigureAwait(true);
        FileBrowseResult roots = await BrowseAsync(client, cluster, "b", new(FromDrives: fromDrives)).ConfigureAwait(true);
        Assert.Null(roots.Problem);
        Assert.Contains(roots.Entries, entry => entry.IsDirectory && string.Equals(entry.Path, Path.GetPathRoot(destinationRoot), StringComparison.OrdinalIgnoreCase));

        // 同一PC・同一OSアカウントの別Coreでも、同じ読取り権限を使います。
        foreach (string peer in new[] { "a", "b" })
        {
            FileBrowseResult source = await BrowseAsync(client, cluster, peer, new(sourceRoot, FromDrives: fromDrives)).ConfigureAwait(true);
            Assert.Null(source.Problem);
            Assert.Equal(sourcePath, Assert.Single(source.Entries).Path);
        }
        FileBrowseResult empty = await BrowseAsync(client, cluster, "b", new(destinationRoot, FromDrives: fromDrives)).ConfigureAwait(true);
        Assert.Null(empty.Problem);
        Assert.Empty(empty.Entries);
        Assert.Equal(second.DirectoryPath, empty.Parent);
        foreach (string path in new[] { "relative/path", Path.Combine(destinationRoot, "missing") })
        {
            FileBrowseResult invalid = await BrowseAsync(client, cluster, "b", new(path, FromDrives: fromDrives)).ConfigureAwait(true);
            Assert.NotNull(invalid.Problem);
            Assert.Empty(invalid.Entries);
        }
        await VerifySelectedTransferAsync(first, sourcePath, destination).ConfigureAwait(true);
    }

    /// <summary>別々のCore実体を参加させ、ClientHostの実UDP発見を待ちます。</summary>
    /// <returns>指定クラスタの二Peerを管理画面から選択できる状態です。</returns>
    private static async Task DiscoverAsync(CorePeer first, CorePeer second, ClientHostProcess client, string cluster, int port, CancellationToken cancellationToken = default)
    {
        await first.StartAsync(cancellationToken).ConfigureAwait(true);
        await second.StartAsync(cancellationToken).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2, cancellationToken).ConfigureAwait(true);
        await client.StartAsync(cancellationToken).ConfigureAwait(true);
        using HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: cancellationToken).ConfigureAwait(true);
        binding.EnsureSuccessStatusCode();
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true);
            return clusters?.Any(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal) && item.Peers.Count == 2) == true;
        }, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>選択ノードから転送元・宛先を固定し、ID未指定の次回実行を新しい転送として確認します。</summary>
    /// <returns>初回と内容更新後の二回の実転送結果です。</returns>
    private static async Task VerifySelectedTransferAsync(CorePeer first, string sourcePath, string destination, CancellationToken cancellationToken = default)
    {
        WorkflowDefinition workflow = new("selected-transfer", "Selected peer and paths", [
            new("peer", "value.peer", JsonSerializer.SerializeToElement(new { peerId = "a" })),
            new("source", "value.file", JsonSerializer.SerializeToElement(new { peerId = "a", path = sourcePath })),
            new("destination", "value.file", JsonSerializer.SerializeToElement(new { peerId = "b", path = destination })),
            new("transfer", "file.transfer", JsonSerializer.SerializeToElement(new { overwrite = true }), Target: new(EExecutionScope.One, ["b"])),
        ], [new("peer", "value", "transfer", "$settings.sourcePeer"), new("source", "value", "transfer", "$settings.sourcePath"), new("destination", "value", "transfer", "$settings.path")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null), cancellationToken).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved, ContractJson.Options));
        Assert.NotNull(saved.Revision);
        await TransferAsync(first, workflow.Id, saved.Revision, cancellationToken).ConfigureAwait(true);
        Assert.Equal("first content", await File.ReadAllTextAsync(destination, cancellationToken).ConfigureAwait(true));
        await File.WriteAllTextAsync(sourcePath, "second content", cancellationToken).ConfigureAwait(true);
        await TransferAsync(first, workflow.Id, saved.Revision, cancellationToken).ConfigureAwait(true);
        Assert.Equal("second content", await File.ReadAllTextAsync(destination, cancellationToken).ConfigureAwait(true));
    }

    /// <summary>明示ピアへ読み取り専用の一覧要求を送ります。</summary>
    /// <returns>画面が利用する実際の応答です。</returns>
    private static async Task<FileBrowseResult> BrowseAsync(ClientHostProcess client, string cluster, string peer, FileBrowseRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Post, "api/files/browse", request, cluster, peer, cancellationToken: cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileBrowseResult>(ContractJson.Options, cancellationToken).ConfigureAwait(true))!;
    }

    /// <summary>新しい明示実行を開始し、遠隔での結果まで確認します。</summary>
    /// <returns>転送完了です。</returns>
    private static async Task TransferAsync(CorePeer peer, string workflow, string revision, CancellationToken cancellationToken = default)
    {
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow, revision);
        WorkflowRunStatus status = await peer.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            status = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !status.Running;
        }, cancellationToken).ConfigureAwait(true);
        Assert.True(status.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(status, ContractJson.Options));
        Assert.Equal("b", Assert.Single(status.Report!.Nodes.Single(node => string.Equals(node.NodeId, "transfer", StringComparison.Ordinal)).Targets).PeerId);
    }
}
