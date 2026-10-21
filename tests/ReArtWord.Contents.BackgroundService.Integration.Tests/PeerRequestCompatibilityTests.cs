using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>更新前後の実Coreを両方向へ配置し、並列書込みと予約解放の互換性を確認します。</summary>
public sealed class PeerRequestCompatibilityTests(ITestOutputHelper output)
{
    /// <summary>旧調停先・新調停先の双方で、別Coreへ80ファイルを一回ずつ保存します。</summary>
    /// <returns>全ファイルの内容、実行完了と、残存予約の不存在です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelFilesCompleteAcrossRollingUpgradeAsync(bool previousCoordinator)
    {
        string? previous = Environment.GetEnvironmentVariable("CONTENTS_REQUEST_COMPAT_CORE");
        string id = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? ownerExecutable = previousCoordinator ? previous
                                                      : null;
        string? targetExecutable = previousCoordinator ? null
                                                       : previous;
        CorePeer owner = new(id, "a", port, true, "owner-host", 10, ownerExecutable);
        CorePeer target = new(id, "b", port, false, "file-host", 1, targetExecutable);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = target.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await owner.StartAsync(timeout.Token).ConfigureAwait(true);
        await target.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement a = await owner.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement b = await target.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return new[] { a, b }.All(state => state.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2);
        }, timeout.Token).ConfigureAwait(true);
        string folder = Path.Combine(target.DirectoryPath, "content", "parallel");
        FileItem[] items = Enumerable.Range(0, 80).Select(index => new FileItem(Path.Combine(folder, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"), "value-" + index)).ToArray();
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("parallel-files", "Parallel files", [
            new("loop", "control.parallelForEach", JsonSerializer.SerializeToElement(new { maxConcurrency = 32 }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(items) }, new(EExecutionScope.One, ["a"])),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = items[0].Path }), Target: new(EExecutionScope.One, ["b"])),
            new("end", "control.forEach.end", empty, Target: new(EExecutionScope.One, ["a"]))],
            [new("loop", "item", "write", "$settings.path", Path: "/Path"), new("loop", "item", "write", "value", Path: "/Text"),
             new("loop", "loop", "end", "loop"), new("write", "value", "end", "value")]);
        WorkflowSaveResult saved = await owner.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        WorkflowRunStatus run = await owner.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), definition.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await owner.GetAsync<WorkflowRunStatus>("runs.read", run.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }, TimeSpan.FromMinutes(2), timeout.Token).ConfigureAwait(true);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        Assert.Equal(items.Length, Directory.GetFiles(folder).Length);
        foreach (FileItem item in items)
        {
            Assert.Equal(item.Text, await File.ReadAllTextAsync(item.Path, timeout.Token).ConfigureAwait(true));
        }
        SqliteRepository repository = new(Path.Combine(owner.DirectoryPath, "state", "state.db"));
        Assert.DoesNotContain(await repository.ListAsync("file:reservations:slots", timeout.Token).ConfigureAwait(true), entry => entry.Value is not null);
        JsonElement health = await owner.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        output.WriteLine(JsonSerializer.Serialize(new { previousCoordinator, previousExecutableSupplied = previous is not null,
            coordinatorVersion = health.GetProperty("buildVersion").GetString(), files = items.Length, owner.DirectoryPath }));
    }

    /// <summary>各反復に固有の出力先と内容です。</summary>
    private sealed record FileItem(string Path, string Text);
}
