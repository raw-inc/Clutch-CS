using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実Coreで、参照カードの保存と元WFへの操作・観測を検証します。</summary>
public sealed class WfDashboardProcessTests
{
    /// <summary>別Peerでも同じ物理参照を扱い、Valueだけの編集を元WFへ反映します。</summary>
    /// <returns>実通信、保存、実行結果と型・版の境界です。</returns>
    [Fact(Skip = "旧仕様: WF完了後もProgressを保持する。現行は完了時に解放し、Preview履歴は維持する。")]
    public async Task CardsObserveAndControlTheirSourceAcrossTwoProcessesAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition source = Source();
        WorkflowSaveResult saved = await SaveAsync(first, source, null, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition other = source with { Id = "other-source", Name = "Other source" };
        await SaveAsync(first, other, null, timeout.Token).ConfigureAwait(true);
        WfDashboardReference[] references = source.Nodes.Select(node => new WfDashboardReference(source.Id, node.Id, node.Type)).ToArray();
        WorkflowDefinition board = new("dashboard", "Operator controls", references.Select((reference, index) => new NodeDefinition("card-" + index, WfDashboardReference.CardType,
            reference.ToSettings(), Position: new(index, 0))).ToArray(), []) { IsDashboard = true };
        board = board with { Nodes = [.. board.Nodes,
            new("comment", WfDashboardLayout.CommentType, WfDashboardLayout.CommentSettings("Cue notes\n次の段へ"), Position: new(5, 0)),
            new("break", WfDashboardLayout.BreakType, WfDashboardLayout.BreakSettings(), Position: new(6, 0))] };
        WorkflowSaveResult boardSaved = await SaveAsync(first, board, null, timeout.Token).ConfigureAwait(true);
        JsonElement restored = await second.GetAsync<JsonElement>("workflows.read", board.Id, timeout.Token).ConfigureAwait(true);
        Assert.True(restored.GetProperty("saved").GetProperty("definition").GetProperty("isDashboard").GetBoolean());
        JsonElement restoredNodes = restored.GetProperty("saved").GetProperty("definition").GetProperty("nodes");
        Assert.Equal(7, restoredNodes.GetArrayLength());
        Assert.Equal("Cue notes\n次の段へ", restoredNodes[5].GetProperty("settings").GetProperty("text").GetString());
        Assert.Equal(WfDashboardLayout.BreakType, restoredNodes[6].GetProperty("type").GetString());
        WorkflowRunStatus refused = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), board.Id, boardSaved.Revision), timeout.Token).ConfigureAwait(true);
        Assert.Equal("run.dashboardNotExecutable", refused.Reason);
        WfDashboardCatalogPage catalog = await second.GetAsync<WfDashboardCatalogPage>("dashboards.catalog", new WfDashboardCatalogQuery("Desk cue"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(4, catalog.Items.Count);
        Assert.Equal(4, catalog.Items.Select(item => item.Reference).Distinct().Count());
        WfDashboardCatalogPage physical = await second.GetAsync<WfDashboardCatalogPage>("dashboards.catalog", new WfDashboardCatalogQuery("preview"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, physical.Items.Count);
        WfDashboardNodeState[] states = [];
        await CorePeer.WaitAsync(async token =>
        {
            states = await second.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery(references), token).ConfigureAwait(true);
            return states.All(state => state.Active);
        }, timeout.Token).ConfigureAwait(true);
        Assert.All(states, state => Assert.True(state.Active, state.Problem));
        Assert.All(states, state => Assert.Null(state.Problem));
        await RunAsync(second, source.Id, saved.Revision!, "button", timeout.Token).ConfigureAwait(true);
        await AssertPreviewAsync(first, "12", timeout.Token).ConfigureAwait(true);
        await AssertPreviewAsync(second, "12", timeout.Token).ConfigureAwait(true);
        NodePreviewSnapshot[] progress = await second.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(source.Id, ["progress"]), timeout.Token).ConfigureAwait(true);
        Assert.Single(progress);
        await RunAsync(first, source.Id, saved.Revision!, "toggle", timeout.Token).ConfigureAwait(true);
        InternalDataReadResult shared = await second.GetAsync<InternalDataReadResult>("internal-data.read",
            new InternalDataTarget(source.Id, EInternalDataScope.Shared, ToggleButtonState.Key("toggle")), timeout.Token).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(shared.ConfirmedValue, out ToggleButtonState toggle));
        Assert.True(toggle.IsOn);
        WfDashboardReference value = references.Single(reference => reference.NodeId is "value");
        WfDashboardValuePreparation invalid = await second.GetAsync<WfDashboardValuePreparation>("dashboards.value-prepare",
            new WfDashboardValueRequest(Guid.NewGuid(), value, saved.Revision!, "not an integer"), timeout.Token).ConfigureAwait(true);
        Assert.Null(invalid.Request);
        WfDashboardValuePreparation prepared = await second.GetAsync<WfDashboardValuePreparation>("dashboards.value-prepare",
            new WfDashboardValueRequest(Guid.NewGuid(), value, saved.Revision!, "34"), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(prepared.Request);
        Assert.Equal(source.Wires, prepared.Request.Definition.Wires);
        WorkflowSaveResult updated = await second.PostAsync<WorkflowSaveResult>("workflows.save", prepared.Request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, updated.Activation.Outcome);
        Assert.Null((await first.GetAsync<WfDashboardValuePreparation>("dashboards.value-prepare",
            new WfDashboardValueRequest(Guid.NewGuid(), value, saved.Revision!, "56"), timeout.Token).ConfigureAwait(true)).Request);
        await RunAsync(first, source.Id, updated.Revision!, "button", timeout.Token).ConfigureAwait(true);
        await AssertPreviewAsync(second, "34", timeout.Token).ConfigureAwait(true);
        WfDashboardNodeState[] refreshed = await first.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery([value]), timeout.Token).ConfigureAwait(true);
        Assert.Equal("34", Assert.Single(refreshed).ValueText);
        Assert.Equal("integer", refreshed[0].ValueType);
        WorkflowDeleteResult deleted = await first.PostAsync<WorkflowDeleteResult>("workflows.delete",
            new WorkflowDeleteRequest(Guid.NewGuid(), source.Id, updated.Revision!), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, deleted.Deleted.Outcome);
        WfDashboardNodeState[] missing = [];
        await CorePeer.WaitAsync(async token =>
        {
            missing = await second.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery(references), token).ConfigureAwait(true);
            return missing.All(state => state.Problem is not null);
        }, timeout.Token).ConfigureAwait(true);
        Assert.All(missing, state => Assert.NotNull(state.Problem));
        Assert.All(missing, state => Assert.False(state.Active));
        Assert.Equal(7, (await second.GetAsync<JsonElement>("workflows.read", board.Id, timeout.Token).ConfigureAwait(true)).GetProperty("saved").GetProperty("definition").GetProperty("nodes").GetArrayLength());
    }

    /// <summary>別Peerの参照操作とValue保存を維持し、完了したProgressだけを回収します。</summary>
    /// <returns>実通信、保存、実行結果と型・版の境界です。</returns>
    [Fact]
    public async Task CardsObserveSourcesAndReleaseCompletedProgressAcrossTwoProcessesAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition source = Source();
        WorkflowSaveResult saved = await SaveAsync(first, source, null, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition other = source with { Id = "other-source", Name = "Other source" };
        await SaveAsync(first, other, null, timeout.Token).ConfigureAwait(true);
        WfDashboardReference[] references = source.Nodes.Select(node => new WfDashboardReference(source.Id, node.Id, node.Type)).ToArray();
        WorkflowDefinition board = new("dashboard", "Operator controls", references.Select((reference, index) => new NodeDefinition("card-" + index, WfDashboardReference.CardType,
            reference.ToSettings(), Position: new(index, 0))).ToArray(), []) { IsDashboard = true };
        board = board with { Nodes = [.. board.Nodes,
            new("comment", WfDashboardLayout.CommentType, WfDashboardLayout.CommentSettings("Cue notes\n次の段へ"), Position: new(5, 0)),
            new("break", WfDashboardLayout.BreakType, WfDashboardLayout.BreakSettings(), Position: new(6, 0))] };
        WorkflowSaveResult boardSaved = await SaveAsync(first, board, null, timeout.Token).ConfigureAwait(true);
        JsonElement restored = await second.GetAsync<JsonElement>("workflows.read", board.Id, timeout.Token).ConfigureAwait(true);
        Assert.True(restored.GetProperty("saved").GetProperty("definition").GetProperty("isDashboard").GetBoolean());
        JsonElement restoredNodes = restored.GetProperty("saved").GetProperty("definition").GetProperty("nodes");
        Assert.Equal(7, restoredNodes.GetArrayLength());
        Assert.Equal("Cue notes\n次の段へ", restoredNodes[5].GetProperty("settings").GetProperty("text").GetString());
        Assert.Equal(WfDashboardLayout.BreakType, restoredNodes[6].GetProperty("type").GetString());
        WorkflowRunStatus refused = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), board.Id, boardSaved.Revision), timeout.Token).ConfigureAwait(true);
        Assert.Equal("run.dashboardNotExecutable", refused.Reason);
        WfDashboardCatalogPage catalog = await second.GetAsync<WfDashboardCatalogPage>("dashboards.catalog", new WfDashboardCatalogQuery("Desk cue"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(4, catalog.Items.Count);
        Assert.Equal(4, catalog.Items.Select(item => item.Reference).Distinct().Count());
        WfDashboardCatalogPage physical = await second.GetAsync<WfDashboardCatalogPage>("dashboards.catalog", new WfDashboardCatalogQuery("preview"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, physical.Items.Count);
        WfDashboardNodeState[] states = [];
        await CorePeer.WaitAsync(async token =>
        {
            states = await second.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery(references), token).ConfigureAwait(true);
            return states.All(state => state.Active);
        }, timeout.Token).ConfigureAwait(true);
        Assert.All(states, state => Assert.True(state.Active, state.Problem));
        Assert.All(states, state => Assert.Null(state.Problem));
        await RunAsync(second, source.Id, saved.Revision!, "button", timeout.Token).ConfigureAwait(true);
        await AssertPreviewAsync(first, "12", timeout.Token).ConfigureAwait(true);
        await AssertPreviewAsync(second, "12", timeout.Token).ConfigureAwait(true);
        NodePreviewSnapshot[] progress = await second.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(source.Id, ["progress"]), timeout.Token).ConfigureAwait(true);
        Assert.Empty(progress);
        await RunAsync(first, source.Id, saved.Revision!, "toggle", timeout.Token).ConfigureAwait(true);
        InternalDataReadResult shared = await second.GetAsync<InternalDataReadResult>("internal-data.read",
            new InternalDataTarget(source.Id, EInternalDataScope.Shared, ToggleButtonState.Key("toggle")), timeout.Token).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(shared.ConfirmedValue, out ToggleButtonState toggle));
        Assert.True(toggle.IsOn);
        WfDashboardReference value = references.Single(reference => reference.NodeId is "value");
        WfDashboardValuePreparation invalid = await second.GetAsync<WfDashboardValuePreparation>("dashboards.value-prepare",
            new WfDashboardValueRequest(Guid.NewGuid(), value, saved.Revision!, "not an integer"), timeout.Token).ConfigureAwait(true);
        Assert.Null(invalid.Request);
        WfDashboardValuePreparation prepared = await second.GetAsync<WfDashboardValuePreparation>("dashboards.value-prepare",
            new WfDashboardValueRequest(Guid.NewGuid(), value, saved.Revision!, "34"), timeout.Token).ConfigureAwait(true);
        Assert.NotNull(prepared.Request);
        Assert.Equal(source.Wires, prepared.Request.Definition.Wires);
        WorkflowSaveResult updated = await second.PostAsync<WorkflowSaveResult>("workflows.save", prepared.Request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, updated.Activation.Outcome);
        Assert.Null((await first.GetAsync<WfDashboardValuePreparation>("dashboards.value-prepare",
            new WfDashboardValueRequest(Guid.NewGuid(), value, saved.Revision!, "56"), timeout.Token).ConfigureAwait(true)).Request);
        await RunAsync(first, source.Id, updated.Revision!, "button", timeout.Token).ConfigureAwait(true);
        await AssertPreviewAsync(second, "34", timeout.Token).ConfigureAwait(true);
        WfDashboardNodeState[] refreshed = await first.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery([value]), timeout.Token).ConfigureAwait(true);
        Assert.Equal("34", Assert.Single(refreshed).ValueText);
        Assert.Equal("integer", refreshed[0].ValueType);
        WorkflowDeleteResult deleted = await first.PostAsync<WorkflowDeleteResult>("workflows.delete",
            new WorkflowDeleteRequest(Guid.NewGuid(), source.Id, updated.Revision!), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, deleted.Deleted.Outcome);
        WfDashboardNodeState[] missing = [];
        await CorePeer.WaitAsync(async token =>
        {
            missing = await second.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery(references), token).ConfigureAwait(true);
            return missing.All(state => state.Problem is not null);
        }, timeout.Token).ConfigureAwait(true);
        Assert.All(missing, state => Assert.NotNull(state.Problem));
        Assert.All(missing, state => Assert.False(state.Active));
        Assert.Equal(7, (await second.GetAsync<JsonElement>("workflows.read", board.Id, timeout.Token).ConfigureAwait(true)).GetProperty("saved").GetProperty("definition").GetProperty("nodes").GetArrayLength());
    }

    /// <summary>一つの元WFに、ボタン・Value・表示と独立した共有トグルを置きます。</summary>
    /// <returns>外部APIや実機I/Oを呼ばない試験用WFです。</returns>
    private static WorkflowDefinition Source()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        return new("source", "Source controls", [
            new("value", "value.constant", JsonSerializer.SerializeToElement(new { type = "integer", text = "12" }), LogicalName: "Amount"),
            new("button", "control.button", empty, Inputs: new Dictionary<string, NodeValue> { ["label"] = NodeValue.FromObject("Run cue") }, Target: new(EExecutionScope.One, ["b"]), LogicalName: "Desk cue"),
            new("preview", "control.preview", empty, Target: new(EExecutionScope.All, ["a", "b"]), LogicalName: "Output"),
            new("progress", "control.progress", empty, Inputs: new Dictionary<string, NodeValue> { ["total"] = NodeValue.FromObject(100) }, Target: new(EExecutionScope.One, ["b"]), LogicalName: "Progress"),
            new("toggle", "control.toggleButton", empty, Inputs: new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject(false), ["onValue"] = NodeValue.FromObject(true) }, Target: new(EExecutionScope.One, ["b"]), LogicalName: "Desk cue")],
            [new("value", "value", "button", "value"), new("button", "value", "preview", "value"), new("button", "value", "progress", "current")]);
    }

    /// <summary>両Coreに有効化できた保存だけを後続検証に渡します。</summary>
    /// <returns>保存結果です。</returns>
    private static async Task<WorkflowSaveResult> SaveAsync(CorePeer peer, WorkflowDefinition definition, string? revision, CancellationToken cancellationToken = default)
    {
        WorkflowSaveResult result = await peer.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, revision), cancellationToken).ConfigureAwait(false);
        Assert.True(result.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        return result;
    }

    /// <summary>元ノードを指定して一回開始し、完了を待ちます。</summary>
    /// <returns>副作用を再送しない完了確認です。</returns>
    private static async Task RunAsync(CorePeer peer, string workflow, string revision, string node, CancellationToken cancellationToken = default)
    {
        string type = "control.button";
        string toggle = "toggle";
        if (string.Equals(node, toggle, StringComparison.Ordinal))
        {
            type = "control.toggleButton";
        }
        await CorePeer.WaitAsync(async token =>
        {
            WfDashboardNodeState[] states = await peer.GetAsync<WfDashboardNodeState[]>("dashboards.nodes", new WfDashboardNodeQuery([new(workflow, node, type)]), token).ConfigureAwait(false);
            return states is [{ Active: true } state] && string.Equals(state.Revision, revision, StringComparison.Ordinal);
        }, cancellationToken).ConfigureAwait(false);
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow, revision, node, new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) });
        WorkflowRunStatus run = await peer.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(false);
        Assert.True(run.Running, JsonSerializer.Serialize(run));
        await CorePeer.WaitAsync(async token =>
        {
            run = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !run.Running;
        }, cancellationToken).ConfigureAwait(false);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
    }

    /// <summary>実行結果が、指定Peerの元Previewへ届いたことを確認します。</summary>
    /// <returns>プレビューの本文です。</returns>
    private static async Task AssertPreviewAsync(CorePeer peer, string expected, CancellationToken cancellationToken = default)
    {
        NodePreviewHistoryEntry[] history = await peer.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery("source", "preview"), cancellationToken).ConfigureAwait(false);
        Assert.NotEmpty(history);
        Assert.Equal(expected, history[0].Text);
    }
}
