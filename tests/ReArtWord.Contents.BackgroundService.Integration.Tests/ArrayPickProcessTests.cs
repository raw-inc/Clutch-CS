using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実Coreで、実行時に届く配列や乱数範囲を選択して保存します。</summary>
public sealed class ArrayPickProcessTests
{
    /// <summary>同じ抽選結果を別PCの二つの後続へ共有します。</summary>
    /// <returns>実通信と保存内容の確認です。</returns>
    [Theory]
    [InlineData("data.pick")]
    [InlineData("data.randomPick")]
    [InlineData("math.random")]
    public async Task SelectsRuntimeInputAcrossTwoPeersAsync(string type)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string source = Path.Combine(first.DirectoryPath, "content", "input.json");
        string left = Path.Combine(first.DirectoryPath, "content", "left.txt"), right = Path.Combine(second.DirectoryPath, "content", "right.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(right)!);
        string contents = "[\"selected\"]", expected = "selected";
        Dictionary<string, NodeValue>? inputs = null;
        List<WireDefinition> wires = [new("read", "value", "parse", "value"), new("choose", "value", "left", "value"), new("choose", "value", "right", "value")];
        string randomType = "math.random", pickType = "data.pick";
        if (string.Equals(type, randomType, StringComparison.Ordinal))
        {
            contents = "17";
            expected = "17";
            wires.Add(new("parse", "value", "choose", "$settings.minimum"));
            wires.Add(new("parse", "value", "choose", "$settings.maximum"));
        }
        else
        {
            wires.Add(new("parse", "value", "choose", "value"));
            if (string.Equals(type, pickType, StringComparison.Ordinal))
            {
                inputs = new() { ["index"] = NodeValue.FromObject(0) };
            }
        }
        await File.WriteAllTextAsync(source, contents).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        TargetDefinition a = new(EExecutionScope.One, ["a"]), b = new(EExecutionScope.One, ["b"]);
        WorkflowDefinition definition = new("selection", "Select runtime data", [
            new("read", "file.read", JsonSerializer.SerializeToElement(new { path = source }), Target: a),
            new("parse", "data.json.parse", settings, Target: a), new("choose", type, settings, inputs, Target: b),
            new("left", "file.create", JsonSerializer.SerializeToElement(new { path = left }), Target: a),
            new("right", "file.create", JsonSerializer.SerializeToElement(new { path = right }), Target: b)], wires);
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
        Assert.Equal(expected, await File.ReadAllTextAsync(left).ConfigureAwait(true));
        Assert.Equal(expected, await File.ReadAllTextAsync(right).ConfigureAwait(true));
        Assert.Equal(contents, await File.ReadAllTextAsync(source).ConfigureAwait(true));
    }
}
