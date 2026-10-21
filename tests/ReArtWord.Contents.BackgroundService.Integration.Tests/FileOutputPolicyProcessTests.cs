using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別Coreへの転送でも親作成・上書き・占有スキップを同じ方針で扱います。</summary>
public sealed class FileOutputPolicyProcessTests
{
    /// <summary>実通信と補償準備を通り、保存先のない初回から占有中の再実行まで確認します。</summary>
    /// <returns>転送先内容と各回の実行結果です。</returns>
    [Fact]
    public async Task RemoteTransferCreatesParentsAndPreservesSkippedOutputAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer source = new(cluster, "source-peer", port, true, "source-host");
        CorePeer destination = new(cluster, "destination-peer", port, false, "destination-host");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = ((IAsyncDisposable)source).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = ((IAsyncDisposable)destination).ConfigureAwait(true);
        await source.StartAsync().ConfigureAwait(true);
        await destination.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement first = await source.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement second = await destination.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return Ready(first) && Ready(second);
        }).ConfigureAwait(true);
        string sourcePath = Path.Combine(source.DirectoryPath, "content", "source.bin");
        string output = Path.Combine(destination.DirectoryPath, "content", "new", "nested", "output.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        byte[] firstBytes = Enumerable.Range(0, 80000).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, firstBytes).ConfigureAwait(true);
        await RunAsync(source, sourcePath, output, "overwrite", "error").ConfigureAwait(true);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        byte[] replacement = [7, 4, 1];
        await File.WriteAllBytesAsync(sourcePath, replacement).ConfigureAwait(true);
        await RunAsync(source, sourcePath, output, "overwrite", "error").ConfigureAwait(true);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        await File.WriteAllBytesAsync(sourcePath, firstBytes).ConfigureAwait(true);
        await RunAsync(source, sourcePath, output, "skip", "error").ConfigureAwait(true);
        using (FileStream locked = new(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await RunAsync(source, sourcePath, output, "overwrite", "skip").ConfigureAwait(true);
        }
        Assert.Equal(replacement, await File.ReadAllBytesAsync(output).ConfigureAwait(true));
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(true));
    }

    /// <summary>別Hostを宛先にし、実際の保存・補償準備・転送・確定を一回ずつ行います。</summary>
    /// <returns>完了した一回の検証です。</returns>
    private static async Task RunAsync(CorePeer source, string sourcePath, string output, string existing, string locked)
    {
        string id = Guid.NewGuid().ToString("N");
        WorkflowDefinition workflow = new(id, "output policy", [new("transfer", "file.transfer", JsonSerializer.SerializeToElement(new
        {
            sourceHost = "source-host", sourcePath, path = output, requestId = id, failurePolicy = "rollbackAll", existingFileMode = existing, lockedFileMode = locked,
        }), Target: new(EExecutionScope.One, Peers: ["destination-peer"]))], []);
        WorkflowSaveResult saved = await source.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null)).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow.Id, saved.Revision);
        WorkflowRunStatus result = await source.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token =>
        {
            result = await source.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !result.Running;
        }).ConfigureAwait(false);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
    }

    /// <summary>両Peerが準備済みになってから転送を始めます。</summary>
    /// <returns>実通信で互いを認識しているかです。</returns>
    private static bool Ready(JsonElement status)
        => status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
}
