using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実行後に届く配列を数え、別Coreで要素数を利用します。</summary>
public sealed class CountProcessTests
{
    /// <summary>HTTP本文と同じJSON解析出力を、Countの要求時評価へ渡します。</summary>
    /// <returns>二つの実Core間で解析・集計した結果の確認です。</returns>
    [Theory]
    [InlineData("[]", "0")]
    [InlineData("[null,false,[1,2]]", "3")]
    public async Task CountsRuntimeArraysAcrossPeersAsync(string contents, string expected)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string source = Path.Combine(first.DirectoryPath, "content", "array.json"), destination = Path.Combine(second.DirectoryPath, "content", "count.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(source, contents).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("count-file", "Count array", [
            new("read", "file.read", JsonSerializer.SerializeToElement(new { path = source }), Target: new(EExecutionScope.One, ["a"])),
            new("parse", "data.json.parse", settings, Target: new(EExecutionScope.One, ["b"])),
            new("count", "data.count", settings),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = destination }), Target: new(EExecutionScope.One, ["b"])),
        ], [new("read", "value", "parse", "value"), new("parse", "value", "count", "value"), new("count", "value", "write", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }).ConfigureAwait(true);
        Assert.True(run.Outcome is EOutcome.Succeeded, JsonSerializer.Serialize(run));
        Assert.Equal(expected, await File.ReadAllTextAsync(destination).ConfigureAwait(true));
        Assert.Equal(contents, await File.ReadAllTextAsync(source).ConfigureAwait(true));
    }
}
