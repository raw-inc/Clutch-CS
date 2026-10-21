using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>SubWorkflow定義上の押下と遠隔配置からの参照を、実Core・実通信で確認します。</summary>
public sealed class SubworkflowButtonProcessTests
{
    /// <summary>SubWorkflowの物理名・論理名を検索し、WFDashboard経由と同じAPIで遠隔トグルを操作します。</summary>
    /// <returns>候補抽出・参照解決・有効状態・実操作と共有状態の検証です。</returns>
    [Fact]
    public async Task DashboardSelectsAndOperatesSubworkflowToggleAcrossPeersAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        NodeDefinition toggle = new("child-toggle", "control.toggleButton", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject("OFF"), ["onValue"] = NodeValue.FromObject("ON") }, new(EExecutionScope.One, ["b"]), LogicalName: "Relay switch");
        NodeDefinition output = new("out", SubworkflowInterface.OutputType, JsonSerializer.SerializeToElement(new { name = "selected", valueType = new { type = "string" } }));
        WorkflowDefinition child = new("child-dashboard", "Child controls", [toggle, output], [new(toggle.Id, "value", "out", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), child, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => string.Equals((await second.GetAsync<JsonElement>("workflows.read", child.Id, token).ConfigureAwait(true))
            .GetProperty("effectiveRevision").GetString(), saved.Revision, StringComparison.Ordinal)).ConfigureAwait(true);
        WfDashboardReference reference = new(child.Id, toggle.Id, toggle.Type);
        foreach (string search in new[] { toggle.Id, toggle.LogicalName! })
        {
            WfDashboardCatalogPage page = await second.GetAsync<WfDashboardCatalogPage>("dashboards.catalog", new WfDashboardCatalogQuery(search)).ConfigureAwait(true);
            Assert.Equal(reference, Assert.Single(page.Items).Reference);
        }
        WorkflowDefinition board = new("child-board", "SubWorkflow controls", [new("card", WfDashboardReference.CardType, reference.ToSettings())], []) { IsDashboard = true };
        Assert.Equal(EOutcome.Succeeded, (await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), board, null)).ConfigureAwait(true)).Saved.Outcome);
        WfDashboardNodeState state = Assert.Single(await second.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery([reference])).ConfigureAwait(true));
        Assert.Null(state.Problem);
        Assert.True(state.Active);
        await RunAsync(second, child.Id, toggle.Id, new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        InternalDataReadResult stored = await first.GetAsync<InternalDataReadResult>("internal-data.read", new InternalDataTarget(child.Id, EInternalDataScope.Shared, ToggleButtonState.Key(toggle.Id))).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(stored.ConfirmedValue, out ToggleButtonState confirmed));
        Assert.True(confirmed.IsOn);
    }

    /// <summary>定義上のトグル操作を別PCの配置から読み、普通のButtonも定義上で押せます。</summary>
    /// <returns>保存・反映・手動受付・遠隔読取りの一巡です。</returns>
    [Fact]
    public async Task DefinitionButtonsAndRemotePlacementShareConfirmedToggleAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        NodeDefinition toggle = new("toggle", "control.toggleButton", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject("OFF"), ["onValue"] = NodeValue.FromObject("ON") }, new(EExecutionScope.One, ["b"]));
        NodeDefinition button = new("button", "control.button", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition output = new("out", SubworkflowInterface.OutputType,
            JsonSerializer.SerializeToElement(new { name = "selected", valueType = new { type = "string" } }));
        WorkflowDefinition child = new("child-buttons", "Child buttons", [toggle, button, output], [new("toggle", "value", "out", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), child, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        string type = SubworkflowInterface.CallType(child.Id);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<NodeDescriptor[]>("catalog", token).ConfigureAwait(true))
            .Any(node => string.Equals(node.Type, type, StringComparison.Ordinal))).ConfigureAwait(true);
        WorkflowDefinition parent = new("parent-buttons", "Parent buttons", [new("call", type, JsonSerializer.SerializeToElement(new { }))], []);
        Assert.Equal(EOutcome.Succeeded, (await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), parent, null)).ConfigureAwait(true)).Activation.Outcome);
        string outputId = RecipeNodeIdentity.Child("call", "out");
        ExecutionReport initial = await RunAsync(first, parent.Id, outputId).ConfigureAwait(true);
        Assert.Equal("OFF", Value(initial));
        await RunAsync(first, child.Id, toggle.Id, new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) }).ConfigureAwait(true);
        Assert.Equal("ON", Value(await RunAsync(second, parent.Id, outputId).ConfigureAwait(true)));
        ExecutionReport ordinary = await RunAsync(second, child.Id, button.Id).ConfigureAwait(true);
        Assert.Contains(ordinary.Nodes, node => string.Equals(node.NodeId, button.Id, StringComparison.Ordinal)
            && node.Targets.Any(target => target.Result.Outputs.ContainsKey("start")));
        InternalDataReadResult stored = await first.GetAsync<InternalDataReadResult>("internal-data.read",
            new InternalDataTarget(child.Id, EInternalDataScope.Shared, ToggleButtonState.Key(toggle.Id))).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(stored.ConfirmedValue, out ToggleButtonState state));
        Assert.True(state.IsOn);
        Assert.Single(state.Events);
    }

    /// <summary>同じ受付IDを追跡し、実行要求を別IDで再送しません。</summary>
    /// <returns>元の実行の完了報告です。</returns>
    private static async Task<ExecutionReport> RunAsync(CorePeer peer, string workflow, string? entry,
        IReadOnlyDictionary<string, NodeValue>? inputs = null, CancellationToken cancellationToken = default)
    {
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow, EntryNodeId: entry, Inputs: inputs);
        WorkflowRunStatus status = await peer.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(false);
        Assert.True(status.Running || status.Outcome == EOutcome.Succeeded, status.Reason);
        await CorePeer.WaitAsync(async token =>
        {
            status = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !status.Running && status.Report is not null;
        }, cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, status.Outcome);
        return status.Report!;
    }

    /// <summary>呼出し配置から得た公開出力を確認します。</summary>
    /// <returns>状態を変更せず参照した選択値です。</returns>
    private static string? Value(ExecutionReport report)
    {
        string output = RecipeNodeIdentity.Child("call", "out");
        return Assert.Single(report.Nodes.Single(node => string.Equals(node.NodeId, output, StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString();
    }
}
