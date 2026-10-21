using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>通常NodeExecutorを通らないファイル経路にも開始前の許可を適用します。</summary>
public sealed class FileAdmissionTests
{
    /// <summary>離脱用途拒否または依存競合では、Keep・全対象退避とも実ファイルを変更しません。</summary>
    /// <returns>実UDP準備経路と直接Keepの拒否結果です。</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task FileChangesRequirePermissionBeforeAnyCapture(bool detached, bool transaction, bool capturedConflict)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        FileTestPeer peer = new(Guid.NewGuid().ToString("N"), "one", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        peer.Cluster.LocalDetachedExecutionAllowed = request => request.Kind.StartsWith("file.", StringComparison.Ordinal) || string.Equals(request.Kind, "node.execute", StringComparison.Ordinal);
        await peer.Cluster.StartAsync();
        if (detached)
        {
            await peer.Cluster.DetachAsync();
            peer.Transactions.DetachedOptions = new(FileChange: false);
        }
        else
        {
            if (!capturedConflict)
            {
                peer.Transactions.DependencyCheck = (workflow, node) => "Conflict.FilePolicy";
            }
        }
        string path = Path.Combine(peer.Root, "value.txt");
        await File.WriteAllTextAsync(path, "before");
        NodeDefinition node = new("write", "file.upsert", JsonSerializer.SerializeToElement(new { path, failurePolicy = "rollbackAll" }));
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "demo", "revision", node, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("after") }, "one", peer.Cluster.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        if (capturedConflict)
        {
            ClusterOperationalSettings settings = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
            request = request with { Operational = new(settings, ContractJson.Hash(settings), DateTimeOffset.UtcNow, UnresolvedFields: ["FileConflictPolicy"]) };
        }
        EOutcome outcome;
        if (transaction)
        {
            outcome = (await peer.Transactions.ExecuteAsync(new(Guid.NewGuid(), [request]))).Outcome;
        }
        else
        {
            outcome = (await peer.Transactions.ExecuteFileAsync((IActionNode)peer.Registry.Find(node.Type)!, FileTestPeer.Context(request))).Outcome;
        }
        Assert.NotEqual(EOutcome.Succeeded, outcome);
        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Empty(await peer.Repository.ListAsync("peer:one:file-prepared"));
    }
}
