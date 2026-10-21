using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>外部ファイルの配列を検索し、選択値または既定値を別Coreへ渡します。</summary>
public sealed class KeyValueLookupProcessTests
{
    /// <summary>データ到着後の純粋評価でも、自動判別と明示方式が反映されます。</summary>
    /// <returns>実Core二台、ファイル読込み・解析・検索・書込みの確認です。</returns>
    [Theory]
    [InlineData("[{\"cue\":\"live\"}]", "auto", "live")]
    [InlineData("[{\"key\":\"cue\",\"value\":\"live\"}]", "auto", "live")]
    [InlineData("[{\"Key\":\"cue\",\"Value\":\"live\"}]", "pairs", "live")]
    [InlineData("[{\"key\":\"cue\",\"value\":\"live\"}]", "objects", "fallback")]
    public async Task LooksUpRuntimeFileContentsWithoutReshapingAsync(string contents, string format, string expected)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string source = Path.Combine(first.DirectoryPath, "content", "lookup.json"), destination = Path.Combine(second.DirectoryPath, "content", "selected.txt");
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
        WorkflowDefinition definition = new("lookup-file", "Lookup file", [
            new("read", "file.read", JsonSerializer.SerializeToElement(new { path = source }), Target: new(EExecutionScope.One, ["a"])),
            new("decode", "text.decode", settings, Target: new(EExecutionScope.One, ["b"])),
            new("parse", "data.json.parse", settings, Target: new(EExecutionScope.One, ["b"])),
            new("lookup", KeyValueLookup.NodeType, JsonSerializer.SerializeToElement(new { format }),
                new Dictionary<string, NodeValue> { ["key"] = NodeValue.FromObject("cue"), ["defaultValue"] = NodeValue.FromObject("fallback") }),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = destination }), Target: new(EExecutionScope.One, ["b"])),
        ], [new("read", "value", "decode", "value"), new("decode", "value", "parse", "value"), new("parse", "value", "lookup", "value"), new("lookup", "value", "write", "value")]);
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
