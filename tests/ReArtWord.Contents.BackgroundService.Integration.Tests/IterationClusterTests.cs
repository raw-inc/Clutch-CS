using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実Core間のUDP配送で反復要素・順序・完了後続の境界を確認します。</summary>
public sealed class IterationClusterTests
{
    /// <summary>別Coreへ配送する要素も開始間隔を守り、全結果を待って後続を一回だけ実行します。</summary>
    /// <returns>実UDP・実プロセスの開始時刻、入力順の集約と後続ログです。</returns>
    [Fact]
    public async Task ParallelStartSlotsApplyAcrossPeersAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(first, second, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition source = Definition(IterationNodeTypes.ParallelForEach, [4, 1, 3, 2], true);
        WorkflowDefinition definition = source with
        {
            Nodes = source.Nodes.Select(node => node.Id switch
            {
                "start" => node with { Settings = JsonSerializer.SerializeToElement(new { maxConcurrency = 3, startIntervalMs = 250 }) },
                "delay" => node with { Settings = JsonSerializer.SerializeToElement(new { milliseconds = 700 }) },
                _ => node,
            }).ToArray(),
        };
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunStatus completed = await RunAsync(first, new(Guid.NewGuid(), definition.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, completed.Outcome);
        ExecutionReport report = Assert.IsType<ExecutionReport>(completed.Report);
        IterationExecutionSummary summary = Assert.IsType<IterationExecutionSummary>(report.Nodes.Single(node => node.NodeId is "start").Iteration);
        Assert.Equal(4, summary.Started);
        DateTimeOffset[] starts = summary.Items.OrderBy(item => item.Index).Select(item => item.Execution.Report!.StartedAt).ToArray();
        for (int index = 1; index < starts.Length; index++)
        {
            Assert.True(TimeSpan.FromMilliseconds(180) <= starts[index] - starts[index - 1]);
        }
        int[] expected = [40, 10, 30, 20];
        Assert.Equal(expected, report.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result.Outputs["value"].Json.EnumerateArray().Select(value => value.GetInt32()));
        LogQueryResult? logs = null;
        await CorePeer.WaitAsync(async token =>
        {
            logs = await second.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: definition.Id, NodeId: "after"), token).ConfigureAwait(true);
            return logs.Entries.Count == 1;
        }, timeout.Token).ConfigureAwait(true);
        Assert.Single(logs!.Entries);
    }

    /// <summary>別Peerの完了信号で明示遷移し、複数結果を同じ入力順に集約します。</summary>
    /// <returns>実Core・実UDPを経由したcontinue/breakと後続一回の確認です。</returns>
    [Theory]
    [InlineData("control.forEach", "continue")]
    [InlineData("control.forEach", "break")]
    [InlineData("control.parallelForEach", "break")]
    public async Task EndControlsAndMultipleResultsWorkAcrossPeerBoundariesAsync(string type, string control)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(first, second, timeout.Token).ConfigureAwait(true);
        int[] values = [1, 2, 3, 4, 5];
        WorkflowDefinition source = Definition(type, values, true);
        WorkflowDefinition document = source with
        {
            Wires = [.. source.Wires, new("start", "index", "end", "value2"), new("bodyLog", CompletionPorts.Signal, "end", control)],
        };
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), document, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        WorkflowRunStatus completed = await RunAsync(first, new(Guid.NewGuid(), document.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        Assert.True(completed.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(completed));
        ExecutionReport report = Assert.IsType<ExecutionReport>(completed.Report);
        IterationExecutionSummary summary = Assert.IsType<IterationExecutionSummary>(report.Nodes.Single(node => node.NodeId is "start").Iteration);
        int expected = 5;
        if (string.Equals(control, IterationNodeTypes.BreakPort, StringComparison.Ordinal))
        {
            int maximum = 1;
            if (string.Equals(type, IterationNodeTypes.ParallelForEach, StringComparison.Ordinal))
            {
                maximum = 3;
            }
            Assert.InRange(summary.Started, 1, maximum);
            expected = summary.Started;
        }
        Assert.Equal(expected, summary.Started);
        IReadOnlyDictionary<string, NodeValue> outputs = Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs;
        Assert.Equal(Enumerable.Range(1, expected).Select(value => value * 10), outputs["value"].Json.EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(Enumerable.Range(0, expected), outputs["value2"].Json.EnumerateArray().Select(value => value.GetInt32()));
        LogQueryResult? logs = null;
        await CorePeer.WaitAsync(async token =>
        {
            logs = await second.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: document.Id, NodeId: "after"), token).ConfigureAwait(true);
            return logs.Entries.Count == 1;
        }, timeout.Token).ConfigureAwait(true);
        Assert.Single(logs!.Entries);
        Assert.Equal(outputs["value"].Json.GetRawText(), logs.Entries[0].Data!.Json.GetRawText());
    }

    /// <summary>引数なしC#を持つレシピも、同値の各要素ごとに別Peerで生成します。</summary>
    /// <returns>公開入力からの暗黙反復を実Core・実UDP・実C#プロセスで確認します。</returns>
    [Theory]
    [InlineData("control.forEach")]
    [InlineData("control.parallelForEach")]
    public async Task RecipeRootsGeneratePerElementOnTheRemotePeerAsync(string type)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        string code = "public static string Run() => Guid.NewGuid().ToString(\"N\");";
        ScriptAnalysis analysis = await first.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("csharp", code)).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition recipe = new("generator-recipe", "Generator", [
            new("input", SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name = "unused", valueType = new { type = "string" } })),
            new("generate", "script.csharp", JsonSerializer.SerializeToElement(new { code, analysis }, ContractJson.Options), Target: new(EExecutionScope.One, ["b"])),
            new("output", SubworkflowInterface.OutputType, JsonSerializer.SerializeToElement(new { name = "generated", valueType = new { type = "string" } }))],
            [new("generate", "value", "output", "value")]);
        WorkflowSaveResult registered = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), recipe, null)).ConfigureAwait(true);
        Assert.True(registered.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(registered));
        WorkflowDefinition parent = new("recipe-iteration", "Recipe iteration", [
            new("each", type, empty, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(Enumerable.Repeat("same", 8).ToArray()) }, new(EExecutionScope.One, ["a"])),
            new("recipe", SubworkflowInterface.CallType(recipe.Id), empty), new("end", IterationNodeTypes.End, empty, Target: new(EExecutionScope.One, ["a"]))],
            [new("each", "item", "recipe", "unused"), new("each", "loop", "end", "loop"), new("recipe", "generated", "end", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), parent, null)).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        WorkflowRunStatus run = await RunAsync(first, new(Guid.NewGuid(), parent.Id, saved.Revision)).ConfigureAwait(true);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        ExecutionReport report = Assert.IsType<ExecutionReport>(run.Report);
        string endId = "end", eachId = "each", generated = RecipeNodeIdentity.Child("recipe", "generate");
        JsonElement values = Assert.Single(report.Nodes, node => string.Equals(node.NodeId, endId, StringComparison.Ordinal)).Targets.Single().Result.Outputs["value"].Json;
        Assert.Equal(8, values.GetArrayLength());
        Assert.Equal(8, values.EnumerateArray().Select(value => value.GetString()).Distinct(StringComparer.Ordinal).Count());
        IterationExecutionSummary iterations = Assert.IsType<IterationExecutionSummary>(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, eachId, StringComparison.Ordinal)).Iteration);
        foreach (IterationExecutionResult item in iterations.Items)
        {
            ExecutionReport body = Assert.IsType<ExecutionReport>(item.Execution.Report);
            TargetNodeResult result = Assert.Single(Assert.Single(body.Nodes, node => string.Equals(node.NodeId, generated, StringComparison.Ordinal)).Targets);
            Assert.Equal("b", result.PeerId);
            Assert.Equal(EOutcome.Succeeded, result.Result.Outcome);
        }
    }

    /// <summary>全要素を別Peerで加工し、Endの有無によらず実完了まで待ちます。</summary>
    /// <returns>逐次・並列の実UDP反復と入力順集約、完了後続一回の確認です。</returns>
    [Theory]
    [InlineData("control.forEach", true, false)]
    [InlineData("control.forEach", true, true)]
    [InlineData("control.parallelForEach", true, false)]
    [InlineData("control.parallelForEach", true, true)]
    [InlineData("control.forEach", false, false)]
    [InlineData("control.parallelForEach", false, false)]
    public async Task RemoteBodiesExecuteEachElementAndJoinInInputOrderAsync(string type, bool withEnd, bool scopedFirst)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        JsonElement firstHealth = await first.GetAsync<JsonElement>("health").ConfigureAwait(true);
        JsonElement secondHealth = await second.GetAsync<JsonElement>("health").ConfigureAwait(true);
        Assert.NotEqual(firstHealth.GetProperty("processId").GetInt32(), secondHealth.GetProperty("processId").GetInt32());
        int[] inputs = [4, 1, 3, 2];
        int[] expected = [40, 10, 30, 20];
        if (scopedFirst)
        {
            Array.Fill(inputs, 4);
            Array.Fill(expected, 40);
        }
        WorkflowDefinition definition = Definition(type, inputs, withEnd);
        if (scopedFirst)
        {
            JsonElement empty = JsonSerializer.SerializeToElement(new { });
            definition = definition with
            {
                Nodes = [.. definition.Nodes,
                    new("prepare", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 10 }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(true) }, Target: new(EExecutionScope.One, ["a"])),
                    new("skip", "control.ifStart", empty, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["condition"] = NodeValue.FromObject(false) }, Target: new(EExecutionScope.One, ["a"])),
                    new("merge", "control.signal.first", empty, Target: new(EExecutionScope.One, ["a"]))],
                Wires = [.. definition.Wires, new("prepare", CompletionPorts.Signal, "merge", "input1"), new("skip", "start", "merge", "input2"), new("merge", "start", "multiply", "trigger")],
            };
        }
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        WorkflowRunStatus completed = await RunAsync(first, new WorkflowRunRequest(Guid.NewGuid(), definition.Id, saved.Revision)).ConfigureAwait(true);
        Assert.True(completed.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(completed));
        ExecutionReport report = Assert.IsType<ExecutionReport>(completed.Report);
        NodeExecutionReport start = Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "start", StringComparison.Ordinal));
        Assert.Equal("a", Assert.Single(start.Targets).PeerId);
        IterationExecutionSummary summary = Assert.IsType<IterationExecutionSummary>(start.Iteration);
        Assert.Equal(inputs.Length, summary.Count);
        Assert.Equal(inputs.Length, summary.Started);
        Assert.Equal(Enumerable.Range(0, inputs.Length), summary.Items.Select(item => item.Index));
        foreach (IterationExecutionResult item in summary.Items)
        {
            ExecutionReport body = Assert.IsType<ExecutionReport>(item.Execution.Report);
            Assert.Equal(EOutcome.Succeeded, body.Outcome);
            NodeExecutionReport multiply = Assert.Single(body.Nodes, node => string.Equals(node.NodeId, "multiply", StringComparison.Ordinal));
            TargetNodeResult remote = Assert.Single(multiply.Targets);
            Assert.Equal("b", remote.PeerId);
            Assert.Equal(EOutcome.Succeeded, remote.Result.Outcome);
            Assert.Equal(expected[item.Index], remote.Result.Outputs["value"].Json.GetInt32());
        }
        LogQueryResult bodyLogs = await second.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: definition.Id, NodeId: "bodyLog")).ConfigureAwait(true);
        Assert.Equal(inputs.Length, bodyLogs.Entries.Count);
        Assert.All(bodyLogs.Entries, entry => Assert.Equal("b", entry.PeerId));
        Assert.Equal(expected.Order(), bodyLogs.Entries.Select(entry => entry.Data!.Json.GetInt32()).Order());
        LogQueryResult afterLogs = await second.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: definition.Id, NodeId: "after")).ConfigureAwait(true);
        if (withEnd)
        {
            NodeExecutionReport end = Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "end", StringComparison.Ordinal));
            TargetNodeResult joined = Assert.Single(end.Targets);
            Assert.Equal("a", joined.PeerId);
            Assert.Equal(expected, joined.Result.Outputs["value"].Json.EnumerateArray().Select(value => value.GetInt32()));
            WorkflowLogEntry after = Assert.Single(afterLogs.Entries);
            Assert.Equal("b", after.PeerId);
            Assert.Equal(expected, after.Data!.Json.EnumerateArray().Select(value => value.GetInt32()));
            Assert.Equal(EOutcome.Succeeded, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "after", StringComparison.Ordinal)).Outcome);
        }
        else
        {
            Assert.Empty(afterLogs.Entries);
            Assert.DoesNotContain(report.Nodes, node => string.Equals(node.NodeId, "end", StringComparison.Ordinal));
        }
    }

    /// <summary>開始・完了と別Peerの反復ボディを明示的に配線します。</summary>
    /// <returns>End省略時も全ボディを実行するワークフローです。</returns>
    private static WorkflowDefinition Definition(string type, int[] values, bool withEnd)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        JsonElement settings = type switch
        {
            "control.parallelForEach" => JsonSerializer.SerializeToElement(new { maxConcurrency = 3 }),
            _ => empty,
        };
        List<NodeDefinition> nodes =
        [
            new("start", type, settings, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(values) }, new TargetDefinition(EExecutionScope.One, ["a"])),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 150 }), Target: new TargetDefinition(EExecutionScope.One, ["b"])),
            new("multiply", "math.multiply", empty, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["right"] = NodeValue.FromObject(10) }, new TargetDefinition(EExecutionScope.One, ["b"])),
            new("bodyLog", "control.log", JsonSerializer.SerializeToElement(new { level = "INFO", message = "remote iteration element completed" }), Target: new TargetDefinition(EExecutionScope.One, ["b"])),
        ];
        List<WireDefinition> wires =
        [
            new("start", "item", "delay", "value"),
            new("delay", "value", "multiply", "left"),
            new("multiply", "value", "bodyLog", "value"),
        ];
        if (withEnd)
        {
            nodes.Add(new("end", "control.forEach.end", empty, Target: new TargetDefinition(EExecutionScope.One, ["a"])));
            nodes.Add(new("after", "control.log", JsonSerializer.SerializeToElement(new { level = "INFO", message = "all remote iteration elements completed" }), Target: new TargetDefinition(EExecutionScope.One, ["b"])));
            wires.Add(new("start", "loop", "end", "loop"));
            wires.Add(new("bodyLog", "value", "end", "value"));
            wires.Add(new("end", "value", "after", "value"));
        }
        return new WorkflowDefinition("remote-iteration", "remote iteration completion", nodes, wires);
    }

    /// <summary>管理受付を完了と見なさず元の操作IDを照会します。</summary>
    /// <returns>反復全体の完了結果です。</returns>
    private static async Task<WorkflowRunStatus> RunAsync(CorePeer peer, WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        WorkflowRunStatus current = await peer.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(true);
        if (current.Running)
        {
            await CorePeer.WaitAsync(async token =>
            {
                current = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                return !current.Running;
            }, cancellationToken).ConfigureAwait(true);
        }
        return current;
    }

    /// <summary>固有クラスタ内の二つの実Peerが同期を終えるまで待ちます。</summary>
    /// <returns>双方から二つのReadyが確認できた状態です。</returns>
    private static Task ReadyAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        JsonElement firstStatus = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        JsonElement secondStatus = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        return firstStatus.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
            && secondStatus.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
    }, cancellationToken);
}
