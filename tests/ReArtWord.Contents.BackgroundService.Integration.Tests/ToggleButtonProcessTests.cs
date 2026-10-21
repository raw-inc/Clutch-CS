using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>共有トグルの操作・遠隔からの参照・再起動後の保持を実Coreで確認します。</summary>
public sealed class ToggleButtonProcessTests
{
    /// <summary>未受信の本番枝を残したまま別PCでダミー側へ切り替え、値だけを読み取ります。</summary>
    /// <returns>実通信でも候補の欠落と正常値の区別が保持されます。</returns>
    [Fact]
    public async Task RemoteToggleReadsDummyWhileTheLiveInputHasNotArrived()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        WorkflowDefinition definition = new("toggle-process", "Remote missing toggle input",
            [ConditionalNode("button", "control.button", "a"), ConditionalNode("live", "control.button", "b"),
             ConditionalNode("comment", "value.comment", "b"), ConditionalNode("dummy", "value.constant", "b", new { text = "dummy" }),
             ConditionalNode("toggle", "control.toggleButton", "b"), ConditionalNode("read", "control.pass", "a")],
            [new("button", "start", "read", "trigger"), new("live", "value", "comment", "value"), new("comment", "value", "toggle", "offValue"),
             new("dummy", "value", "toggle", "onValue"), new("toggle", "value", "read", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        ExecutionReport unavailable = await RunAsync(first, "button", expected: EOutcome.Failed).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, unavailable.Outcome);
        ExecutionReport changed = await RunAsync(first, "toggle", new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, changed.Outcome);
        Assert.Equal("dummy", Value(changed, "toggle"));
        ExecutionReport read = await RunAsync(second, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, read.Outcome);
        Assert.Equal("dummy", Value(read, "read"));
        Assert.Equal(EOutcome.NotExecuted, read.Nodes.Single(node => node.NodeId is "live").Outcome);
        Assert.DoesNotContain(read.Nodes.Single(node => node.NodeId is "toggle").Targets, target => target.Result.Outputs.ContainsKey("start"));
        InternalDataTarget stateTarget = new(definition.Id, EInternalDataScope.Shared, ToggleButtonState.Key("toggle"));
        InternalDataReadResult shared = await first.GetAsync<InternalDataReadResult>("internal-data.read", stateTarget).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(shared.ConfirmedValue, out ToggleButtonState state));
        Assert.True(state.IsOn);
        Assert.Single(state.Events);
    }

    /// <summary>条件分岐と合流を遠隔PCへ置き、値参照の指定が実通信でも失われないことを確認します。</summary>
    /// <returns>別ボタンからの参照と実際のトグル変更を分けた結果です。</returns>
    [Fact]
    public async Task ButtonReadsRemoteConditionalMergeWithoutEmittingItsSignal()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        WorkflowDefinition definition = new("toggle-process", "Remote conditional values", [
            ConditionalNode("button", "control.button", "a"), ConditionalNode("toggle", "control.toggleButton", "b") with
            {
                Inputs = new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject(false), ["onValue"] = NodeValue.FromObject(true) },
            },
            ConditionalNode("off", "control.ifNotStart", "b"), ConditionalNode("on", "control.ifStart", "b"),
            ConditionalNode("stub", "value.constant", "b", new { text = "stub" }), ConditionalNode("production", "value.constant", "b", new { text = "production" }),
            ConditionalNode("merge", "control.valueMerge", "b"), ConditionalNode("read", "control.pass", "a"), ConditionalNode("changed", "control.pass", "a"),
            ConditionalNode("unrelated-loop", "control.forEach", "b") with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(Array.Empty<int>()) } },
        ], [new("button", "start", "read", "trigger"), new("toggle", "value", "off", "condition"), new("toggle", "value", "on", "condition"),
            new("off", "start", "stub", "trigger"), new("on", "start", "production", "trigger"), new("stub", "value", "merge", "input1"),
            new("production", "value", "merge", "input2"), new("merge", "value", "read", "value"), new("merge", "start", "changed", "trigger"), new("merge", "value", "changed", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        ExecutionReport initial = await RunAsync(first, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, initial.Outcome);
        Assert.Equal("stub", Value(initial, "read"));
        Assert.Equal(EOutcome.NotExecuted, initial.Nodes.Single(node => node.NodeId is "changed").Outcome);
        ExecutionReport changed = await RunAsync(first, "toggle", new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        Assert.Equal("production", Value(changed, "changed"));
        ExecutionReport next = await RunAsync(first, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
        Assert.Equal("production", Value(next, "read"));
        Assert.Equal(EOutcome.NotExecuted, next.Nodes.Single(node => node.NodeId is "changed").Outcome);
    }

    /// <summary>実行先PCを固定した条件付き値の試験ノードです。</summary>
    /// <returns>通常の保存APIで検証する定義です。</returns>
    private static NodeDefinition ConditionalNode(string id, string type, string peer, object? settings = null)
        => new(id, type, JsonSerializer.SerializeToElement(settings ?? new { }), Target: new(EExecutionScope.One, [peer]));

    /// <summary>同じ共有状態を参照し、遠隔の値要求で反転せず、実信号でだけ変化します。</summary>
    /// <returns>二つの実プロセスと永続状態の確認です。</returns>
    [Fact]
    public async Task SharedStateSurvivesRestartAndRemoteReadsDoNotToggle()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        WorkflowDefinition definition = new("toggle-process", "Toggle process", [
            new("button", "control.button", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
            new("toggle", "control.toggleButton", JsonSerializer.SerializeToElement(new { }),
                new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject("stub"), ["onValue"] = NodeValue.FromObject("production") }, Target: new(EExecutionScope.One, ["b"])),
            new("read", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
            new("changed", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
        ], [new("button", "start", "toggle", "trigger"), new("toggle", "value", "read", "value"), new("toggle", "start", "changed", "trigger"), new("toggle", "value", "changed", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        ExecutionReport initial = await RunAsync(first, "read").ConfigureAwait(true);
        Assert.Equal("stub", Value(initial, "read"));
        Assert.Equal(EOutcome.NotExecuted, initial.Nodes.Single(node => node.NodeId is "changed").Outcome);
        ExecutionReport pressed = await RunAsync(first, "toggle", new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        Assert.Equal("production", Value(pressed, "changed"));
        InternalDataTarget stateTarget = new(definition.Id, EInternalDataScope.Shared, ToggleButtonState.Key("toggle"));
        InternalDataReadResult shared = await first.GetAsync<InternalDataReadResult>("internal-data.read", stateTarget).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(shared.ConfirmedValue, out ToggleButtonState before));
        Assert.True(before.IsOn);
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        ExecutionReport read = await RunAsync(first, "read").ConfigureAwait(true);
        Assert.Equal("production", Value(read, "read"));
        Assert.Equal(EOutcome.NotExecuted, read.Nodes.Single(node => node.NodeId is "changed").Outcome);
        ExecutionReport signaled = await RunAsync(first, "button").ConfigureAwait(true);
        Assert.Equal("stub", Value(signaled, "changed"));
        InternalDataReadResult after = await second.GetAsync<InternalDataReadResult>("internal-data.read", stateTarget).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(after.ConfirmedValue, out ToggleButtonState final));
        Assert.False(final.IsOn);
        Assert.Equal(2, final.Events.Count);
    }

    /// <summary>再起動前の発見情報を準備完了として採用しません。</summary>
    /// <returns>現プロセスの参加と運用設定の確認です。</returns>
    private static Task ReadyAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        JsonElement health = await second.GetAsync<JsonElement>("health", token).ConfigureAwait(false);
        JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
        Guid instance = health.GetProperty("instanceId").GetGuid();
        return health.GetProperty("ready").GetBoolean() && status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
            && status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("instanceId").GetGuid() == instance)
            && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null;
    }, cancellationToken);

    /// <summary>元の操作IDを再照会し、別の操作として再送しません。</summary>
    /// <returns>保存された実行結果です。</returns>
    private static async Task<ExecutionReport> RunAsync(CorePeer peer, string entry, IReadOnlyDictionary<string, NodeValue>? inputs = null,
        EOutcome expected = EOutcome.Succeeded, CancellationToken cancellationToken = default)
    {
        WorkflowRunRequest request = new(Guid.NewGuid(), "toggle-process", EntryNodeId: entry, Inputs: inputs);
        WorkflowRunStatus result = await peer.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token =>
        {
            result = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !result.Running && result.Report is not null;
        }, cancellationToken).ConfigureAwait(false);
        Assert.Equal(expected, result.Outcome);
        return result.Report!;
    }

    /// <summary>一台の実行先で確認した値だけを取り出します。</summary>
    /// <returns>対象ノードの文字列出力です。</returns>
    private static string? Value(ExecutionReport report, string node) => Assert.Single(report.Nodes.Single(item => string.Equals(item.NodeId, node, StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString();
}
