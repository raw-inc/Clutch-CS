using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>再帰作成を実Core間の配送・ファイル補償準備・確定まで検証します。</summary>
public sealed class RecursiveDirectoryProcessTests
{
    /// <summary>親階層がない宛先も、別Peerの予約と補償範囲を使って作成できます。</summary>
    /// <returns>二つの実プロセス、実通信と実ディレクトリの確認です。</returns>
    [Fact]
    public async Task CreatesNestedDirectoryOnAnotherPeerAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        string path = Path.Combine(second.DirectoryPath, "content", "downloads", "group", "audio");
        NodeDefinition node = new("create", "directory.create", JsonSerializer.SerializeToElement(new { path, failurePolicy = "rollbackAll" }), Target: new(EExecutionScope.One, ["b"]));
        WorkflowDefinition definition = new("recursive-directory", "Recursive directory", [node], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }).ConfigureAwait(true);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        TargetNodeResult result = Assert.Single(Assert.Single(run.Report!.Nodes).Targets);
        Assert.Equal("b", result.PeerId);
        Assert.True(Directory.Exists(path));
    }
}
