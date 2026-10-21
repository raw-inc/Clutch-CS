using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>反映済みレシピの共有、別Peer上の呼出しと再起動後の復元を実通信で確認します。</summary>
public sealed class SubworkflowProcessTests
{
    /// <summary>未反映の保存済み配置でも、実処理を開始せず内部レイアウトを取得できます。</summary>
    /// <returns>二つの実Coreで共有したレシピを読み取り表示する確認です。</returns>
    [Fact]
    public async Task SavedPlacementCanBeInspectedWithoutActivationAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, priority: 10);
        CorePeer second = new(cluster, "b", port, false, priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        NodeDefinition preview = new("preview", "control.preview", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("unexecuted") });
        WorkflowDefinition child = new("preview-child", "Preview child", [preview], []) { IsRecipe = true };
        WorkflowSaveResult savedChild = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), child, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, savedChild.Activation.Outcome);
        string recipe = SubworkflowInterface.CallType(child.Id);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<NodeDescriptor[]>("catalog", token).ConfigureAwait(true)).Any(node => string.Equals(node.Type, recipe, StringComparison.Ordinal))).ConfigureAwait(true);
        NodeDefinition call = new("placement", recipe, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition parent = new("saved-parent", "Saved parent", [call], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), parent, null, EActivationPolicy.Explicit)).ConfigureAwait(true);
        Assert.NotNull(saved.Revision);
        WorkflowRecipePreview view = await first.GetAsync<WorkflowRecipePreview>("workflows.recipe-preview", new WorkflowRecipePreviewQuery(parent.Id, call.Id)).ConfigureAwait(true);
        Assert.False(view.IsRuntime);
        Assert.Equal(saved.Revision, view.Revision);
        Assert.Equal(RecipeNodeIdentity.Child(call.Id, preview.Id), Assert.Single(view.Instance.Layout.Nodes).Id);
        NodePreviewSnapshot[] values = await first.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(parent.Id, [RecipeNodeIdentity.Child(call.Id, preview.Id)])).ConfigureAwait(true);
        Assert.Empty(values);
    }

    /// <summary>別Peerの呼出しノードからさらに配送した子処理まで、親取消を伝えます。</summary>
    /// <returns>子処理を置き去りにせず、元の実行の取消だけで停止する確認です。</returns>
    [Fact]
    public async Task CancellationCrossesTheRemoteCallBoundary()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, priority: 10);
        CorePeer second = new(cluster, "b", port, false, priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        NodeDefinition input = new("input", SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name = "text", valueType = new { type = "string" }, hasDefault = true, defaultValue = "hello" }));
        NodeDefinition delay = new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 60000 }), Target: new(EExecutionScope.One, ["a"]));
        WorkflowDefinition child = new("wait-child", "Wait child", [input, delay], [new("input", "value", "delay", "value")]);
        WorkflowSaveResult savedChild = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), child, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, savedChild.Activation.Outcome);
        string recipe = SubworkflowInterface.CallType(child.Id);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<NodeDescriptor[]>("catalog", token).ConfigureAwait(true)).Any(node => string.Equals(node.Type, recipe, StringComparison.Ordinal))).ConfigureAwait(true);
        NodeDefinition call = new("call", recipe, JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["b"]));
        WorkflowDefinition parent = new("parent", "Parent", [call], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), parent, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRecipePreview preview = await first.GetAsync<WorkflowRecipePreview>("workflows.recipe-preview", new WorkflowRecipePreviewQuery(parent.Id, call.Id)).ConfigureAwait(true);
        Assert.True(preview.IsRuntime);
        Assert.Equal(saved.Revision, preview.Revision);
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), parent.Id, saved.Revision)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("nodeValues").EnumerateArray()
            .Any(item => string.Equals(item.GetProperty("nodeId").GetString(), RecipeNodeIdentity.Child("call", delay.Id), StringComparison.Ordinal) && item.GetProperty("completedAt").ValueKind == JsonValueKind.Null)).ConfigureAwait(true);
        WorkflowCancelResult cancelled = await first.PostAsync<WorkflowCancelResult>("runs.cancel", new WorkflowCancelRequest(Guid.NewGuid(), run.OperationId, run.OwnerInstanceId!.Value)).ConfigureAwait(true);
        Assert.True(cancelled.Requested);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength() == 0
            && (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength() == 0, deadline.Token).ConfigureAwait(true);
        WorkflowRunStatus finished = await first.GetAsync<WorkflowRunStatus>("runs.read", run.OperationId.ToString("D")).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            finished = await first.GetAsync<WorkflowRunStatus>("runs.read", run.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !finished.Running;
        }, deadline.Token).ConfigureAwait(true);
        Assert.False(finished.Running);
        Assert.NotEqual(EOutcome.Succeeded, finished.Outcome);
    }

    /// <summary>子を共有して別Peerで呼び出し、全停止後も同じ公開入出力で実行できます。</summary>
    /// <returns>実Core二つと一回ごとの確定結果です。</returns>
    [Fact]
    public async Task SharedRecipeRunsOnAnotherPeerAndSurvivesRestart()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, priority: 10);
        CorePeer second = new(cluster, "b", port, false, priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        NodeDefinition input = new("input", SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name = "text", valueType = new { type = "string" } }));
        NodeDefinition substring = new("substring", "text.substring", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["startIndex"] = NodeValue.FromObject(1),
            ["length"] = NodeValue.FromObject(3),
        });
        NodeDefinition output = new("output", SubworkflowInterface.OutputType, JsonSerializer.SerializeToElement(new { name = "answer", valueType = new { type = "string" } }));
        WorkflowDefinition child = new("z-child", "Slice text", [input, substring, output], [new("input", "value", "substring", "value"), new("substring", "value", "output", "value")]);
        WorkflowSaveResult savedChild = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), child, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, savedChild.Activation.Outcome);
        string recipe = SubworkflowInterface.CallType(child.Id);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<NodeDescriptor[]>("catalog", token).ConfigureAwait(true)).Any(node => string.Equals(node.Type, recipe, StringComparison.Ordinal))).ConfigureAwait(true);
        NodeDefinition call = new("call", recipe, JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["text"] = NodeValue.FromObject("abcde") },
            new(EExecutionScope.One, ["b"]));
        WorkflowDefinition parent = new("a-parent", "Call child", [call], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), parent, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunStatus result = await RunAsync(first, parent.Id, saved.Revision!).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        TargetNodeResult target = Assert.Single(Assert.Single(result.Report!.Nodes, node => string.Equals(node.NodeId, RecipeNodeIdentity.Child("call", "substring"), StringComparison.Ordinal)).Targets);
        Assert.Equal("b", target.PeerId);
        Assert.Equal("bcd", target.Result.Outputs["value"].GetString());
        WorkflowListPage list = await first.GetAsync<WorkflowListPage>("workflows.list", new WorkflowListQuery()).ConfigureAwait(true);
        Assert.True(Assert.Single(list.Entries, item => string.Equals(item.Id, child.Id, StringComparison.Ordinal)).IsSubworkflow);
        Assert.False(Assert.Single(list.Entries, item => string.Equals(item.Id, parent.Id, StringComparison.Ordinal)).IsSubworkflow);
        await second.StopAsync().ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("workflows.read", parent.Id, token).ConfigureAwait(true))
            .GetProperty("effectiveRevision").ValueKind == JsonValueKind.String).ConfigureAwait(true);
        result = await RunAsync(first, parent.Id, saved.Revision!).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Equal("bcd", Assert.Single(Assert.Single(result.Report!.Nodes, node => string.Equals(node.NodeId, RecipeNodeIdentity.Child("call", "output"), StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString());
    }

    /// <summary>元の一回だけを照会して完了を待ちます。</summary>
    /// <returns>再開始を伴わない実行結果です。</returns>
    private static async Task<WorkflowRunStatus> RunAsync(CorePeer peer, string workflow, string revision)
    {
        Guid id = Guid.NewGuid();
        WorkflowRunStatus status = await peer.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(id, workflow, revision)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            status = await peer.GetAsync<WorkflowRunStatus>("runs.read", id.ToString("D"), token).ConfigureAwait(true);
            return !status.Running;
        }).ConfigureAwait(true);
        return status;
    }

    /// <summary>二つの実体が通常参加するまで待ちます。</summary>
    /// <returns>通信と同期の確認です。</returns>
    private static Task ReadyAsync(CorePeer peer) => CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true))
        .GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2);
}
