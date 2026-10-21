using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>大容量の端から端までの結果を実Core二つで確認します。</summary>
public sealed class ManagementPagingTests
{
    /// <summary>分散実行した結果を圧縮保存し、Coreを停止した後も同じ詳細を復元します。</summary>
    /// <returns>実UDP配送と実プロセス再起動後の診断です。</returns>
    [Fact]
    public async Task CompressedExecutionDetailsSurviveCoordinatorRestartAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        WorkflowDefinition definition = new("compressed-history", "compressed history", [new("value", "control.pass", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new string('x', 6000)) }, new(EExecutionScope.All, ["a", "b"]))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision);
        _ = await first.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        WorkflowRunStatus? run = null;
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, run!.Outcome);
        Assert.Equal(2, Assert.Single(run.Report!.Nodes).Targets.Count);
        SqliteRepository repository = new(Path.Combine(first.DirectoryPath, "state", "state.db"));
        await CorePeer.WaitAsync(async token => (await repository.ListAsync("peer:a:workflow-result-details", token).ConfigureAwait(true)).Count == 1).ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        ExecutionHistoryPage history = await first.GetAsync<ExecutionHistoryPage>("executions.list", new ExecutionHistoryQuery()).ConfigureAwait(true);
        ExecutionResultView restored = Assert.Single(history.Entries);
        Assert.Equal(ContractJson.Hash(run.Report), ContractJson.Hash(restored.Report));
        Assert.Null(restored.DetailReason);
        Assert.Equal(JsonValueKind.Null, (await first.GetAsync<JsonElement>("status").ConfigureAwait(true)).GetProperty("executionHistoryProblem").ValueKind);
    }

    /// <summary>詳細がUDP上限を超えても別Peerと実再起動後に確定成否を照会できます。</summary>
    /// <returns>実プロセス・UDP・永続保存の確認です。</returns>
    [Fact]
    public async Task LargeRunOutcomeSurvivesRemoteQueryAndProcessRestart()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        List<NodeDefinition> nodes = [];
        List<WireDefinition> wires = [];
        for (int index = 0; index < 24; index++)
        {
            IReadOnlyDictionary<string, NodeValue>? inputs = null;
            if (index == 0)
            {
                inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new string('x', 200000)) };
            }
            nodes.Add(new("node" + index, "control.pass", JsonSerializer.SerializeToElement(new { }), inputs, new TargetDefinition(EExecutionScope.One, ["a"])));
            if (0 < index)
            {
                wires.Add(new("node" + (index - 1), "value", "node" + index, "value"));
            }
        }
        WorkflowDefinition definition = new("large-management", "large management", nodes, wires);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision);
        _ = await first.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        WorkflowRunStatus? result = null;
        await CorePeer.WaitAsync(async token =>
        {
            result = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !result.Running;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result!.Outcome);
        Assert.Null(result.Report);
        Assert.Equal(24, result.Summary!.TotalTargets);
        WorkflowRunStatus other = await second.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, other.Outcome);
        Assert.True(other.DetailsUnavailable);
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        WorkflowRunStatus restored = await second.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.Equal(24, restored.Summary!.Targets.Count);
        Assert.True(restored.DetailsUnavailable);
        ExecutionHistoryPage history = await first.GetAsync<ExecutionHistoryPage>("executions.list", new ExecutionHistoryQuery()).ConfigureAwait(true);
        Assert.Contains(history.Entries, item => item.Summary?.Id == request.OperationId);
        WorkflowListPage listing = await first.GetAsync<WorkflowListPage>("workflows.list", new WorkflowListQuery()).ConfigureAwait(true);
        Assert.Contains(listing.Entries, item => string.Equals(item.Id, definition.Id, StringComparison.Ordinal));
    }

    /// <summary>両方の実体が通常参加できるまで待ちます。</summary>
    /// <returns>同期完了の確認です。</returns>
    private static Task ReadyAsync(CorePeer first, CorePeer second) => CorePeer.WaitAsync(async token =>
    {
        JsonElement a = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        JsonElement b = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        return a.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
            && b.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
    });
}
