using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>辞書と各Key-Valueが実Core間を往復し、反復で混線しないことを確認します。</summary>
public sealed class DictionaryProcessTests
{
    /// <summary>別CoreのC#辞書処理を、逐次と並列の各要素で実行します。</summary>
    /// <returns>型情報・全キーの結果・空辞書での完了の確認です。</returns>
    [Theory]
    [InlineData("control.forEach", 12)]
    [InlineData("control.parallelForEach", 12)]
    [InlineData("control.forEach", 0)]
    public async Task DictionaryEntriesStayPairedAcrossProcessesAsync(string iteration, int count)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }, timeout.Token).ConfigureAwait(true);
        string code = "public static async Task<Dictionary<string, int>> Run(Dictionary<string, int> values) { var item = values.Single(); await Task.Delay((12 - item.Value) * 3); return new() { [item.Key] = item.Value * 10 }; }";
        ScriptAnalysis analysis = await second.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("csharp", code), timeout.Token).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        TargetDefinition a = new(EExecutionScope.One, ["a"]), b = new(EExecutionScope.One, ["b"]);
        object[] pairs = Enumerable.Range(0, count).Select(index => (object)new Dictionary<string, int>(StringComparer.Ordinal) { ["key-" + index] = index }).ToArray();
        WorkflowDefinition definition = new("dictionary", "Dictionary", [
            new("dictionary", "value.dictionary", empty, new Dictionary<string, NodeValue> { ["items"] = NodeValue.FromObject(pairs) }),
            new("each", iteration, empty, Target: a),
            new("script", "script.csharp", JsonSerializer.SerializeToElement(new { code, analysis, inputMode = "individual" }, ContractJson.Options), Target: b),
            new("end", IterationNodeTypes.End, empty, Target: a)],
            [new("dictionary", "value", "each", "value"), new("each", "item", "script", ScriptInputPorts.Name("values")),
                new("each", "loop", "end", "loop"), new("script", "value", "end", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }, timeout.Token).ConfigureAwait(true);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        NodeValue result = run.Report!.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result.Outputs["value"];
        Assert.Equal(JsonValueKind.Object, result.Json.ValueKind);
        Dictionary<string, int> actual = result.Json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal);
        Assert.Equal(count, actual.Count);
        foreach (int index in Enumerable.Range(0, count))
        {
            Assert.Equal(index * 10, actual["key-" + index]);
        }
    }
}
