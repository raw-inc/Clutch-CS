using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別Coreプロセスの独立変更を、実UDP管理と再参加で照合します。</summary>
public sealed class PartitionContentTests
{
    /// <summary>状態分岐は依存ノードだけを止め、全候補の明示解消後に正常参加へ戻ります。</summary>
    /// <returns>実二Coreの保存・分岐・独立実行・解消結果です。</returns>
    [Fact]
    public async Task StateForkOnlyBlocksDependentNodeUntilExplicitResolution()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(id, "a", port, true);
        CorePeer second = new(id, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync();
        await second.StartAsync();
        await BothAsync(first, second, "ready");
        WorkflowDefinition workflow = new("state-fork", "state fork", [
            new("read", "state.get", JsonSerializer.SerializeToElement(new { scope = "workflow", key = "key" })),
            Pass("pass"),
        ], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null));
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        InternalDataTarget target = new(workflow.Id, EInternalDataScope.Shared, "key");
        InternalDataWriteResult initial = await WriteAsync(first, target, null, 1);
        Assert.True(initial.Result.Applied);
        string version = (await second.GetAsync<InternalDataReadResult>("internal-data.read", target)).ConfirmedRevision!;
        Assert.NotNull(version);
        await second.StopAsync();
        await CountAsync(first, "ready", 1);
        Assert.True((await WriteAsync(first, target, version, 2)).Result.Applied);
        await first.StopAsync();
        await second.StartAsync();
        await CountAsync(second, "ready", 1);
        Assert.Equal(1, (await second.GetAsync<InternalDataReadResult>("internal-data.read", target)).ConfirmedValue!.Json.GetInt32());
        Assert.True((await WriteAsync(second, target, version, 3)).Result.Applied);
        await second.StopAsync();
        await first.StartAsync();
        await second.StartAsync();
        await BothAsync(first, second, "degraded");
        WorkflowRunStatus blocked = await RunAsync(first, new(Guid.NewGuid(), workflow.Id, saved.Revision, "read"));
        Assert.Equal(EOutcome.NotExecuted, blocked.Outcome);
        Assert.NotNull(blocked.Report);
        for (int run = 0; run < 3; run++)
        {
            WorkflowRunStatus independent = await RunAsync(first, new(Guid.NewGuid(), workflow.Id, saved.Revision, "pass"));
            Assert.True(independent.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(new { independent, first = await first.GetAsync<JsonElement>("status"), second = await second.GetAsync<JsonElement>("status") }));
        }
        ConflictListPage page = await first.GetAsync<ConflictListPage>("conflicts.list");
        ConflictListSummary summary = Assert.Single(page.Entries);
        StateConflict conflict = (await first.GetAsync<ConflictDetail>("conflicts.detail", new ConflictDetailRequest(summary.Id))).Conflict!;
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Contains(conflict.Candidates, candidate => candidate.Value!.Json.GetInt32() == 2);
        StateConflictCandidate selected = conflict.Candidates.Single(candidate => candidate.Value!.Json.GetInt32() == 3);
        ConflictResolveResult resolved = await first.PostAsync<ConflictResolveResult>("conflicts.resolve", new ConflictResolveRequest(Guid.NewGuid(), conflict.Id, conflict.Candidates.Select(candidate => candidate.Version).ToArray(), selected.Version));
        Assert.Equal(EOutcome.Succeeded, resolved.Shared.Outcome);
        await BothAsync(first, second, "ready");
        Assert.Empty((await first.GetAsync<ConflictListPage>("conflicts.list")).Entries);
        Assert.Equal(EOutcome.Succeeded, (await RunAsync(first, new(Guid.NewGuid(), workflow.Id, saved.Revision, "read"))).Outcome);
        Assert.Equal(3, (await second.GetAsync<InternalDataReadResult>("internal-data.read", target)).ConfirmedValue!.Json.GetInt32());
    }

    /// <summary>離脱Coreのローカル保存と実行は共有正本を変えず、明示再参加後に統合します。</summary>
    /// <returns>実二Coreのローカル運転と共有再開です。</returns>
    [Fact]
    public async Task DetachedWorkflowExecutesLocallyThenMergesOnRejoin()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(id, "a", port, true);
        CorePeer second = new(id, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync();
        await second.StartAsync();
        await BothAsync(first, second, "ready");
        WorkflowDefinition definition = new("local-workflow", "shared", [Pass("pass")], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null));
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        await second.PostAsync<JsonElement>("cluster.detach", new { });
        WorkflowSaveResult local = await second.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition with { Name = "local" }, saved.Revision));
        Assert.True(local.LocalOnly);
        Assert.Equal(EOutcome.Succeeded, local.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, local.Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, local.Activation.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await RunAsync(second, new(Guid.NewGuid(), definition.Id, local.Revision))).Outcome);
        Assert.Equal("shared", (await first.GetAsync<JsonElement>("workflows.read", definition.Id)).GetProperty("saved").GetProperty("definition").GetProperty("name").GetString());
        await second.PostAsync<JsonElement>("cluster.rejoin", new { });
        await BothAsync(first, second, "ready");
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement current = await first.GetAsync<JsonElement>("workflows.read", definition.Id, token).ConfigureAwait(true);
            return string.Equals(current.GetProperty("saved").GetProperty("definition").GetProperty("name").GetString(), "local", StringComparison.Ordinal)
                && string.Equals(current.GetProperty("effectiveRevision").GetString(), local.Revision, StringComparison.Ordinal);
        });
        Assert.Equal(EOutcome.Succeeded, (await RunAsync(first, new(Guid.NewGuid(), definition.Id, local.Revision))).Outcome);
    }

    /// <summary>独立した有限処理へ既定入力を渡します。</summary>
    /// <returns>実行結果を観測するノードです。</returns>
    private static NodeDefinition Pass(string id) => new(id, "control.pass", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(42) });

    /// <summary>保存版を明示した内部状態更新を送ります。</summary>
    /// <returns>保存と現在値採用の区別です。</returns>
    private static Task<InternalDataWriteResult> WriteAsync(CorePeer peer, InternalDataTarget target, string? version, int value) => peer.PostAsync<InternalDataWriteResult>("internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), target, version, NodeValue.FromObject(value)));

    /// <summary>新しい実行IDを作らず元受付の実終了を待ちます。</summary>
    /// <returns>元要求の結果です。</returns>
    private static async Task<WorkflowRunStatus> RunAsync(CorePeer peer, WorkflowRunRequest request)
    {
        WorkflowRunStatus result = await peer.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        if (result.Running)
        {
            await CorePeer.WaitAsync(async token =>
            {
                result = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                return !result.Running;
            }).ConfigureAwait(true);
        }
        return result;
    }

    /// <summary>停止した旧実体を成功集合に含めないところまで待ちます。</summary>
    /// <returns>観測した状態集合の成立です。</returns>
    private static Task CountAsync(CorePeer peer, string status, int count) => CorePeer.WaitAsync(async token =>
        (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), status, StringComparison.Ordinal)) == count);

    /// <summary>両実体で同じ二Peerの参加状態を確認します。</summary>
    /// <returns>相互参加の成立です。</returns>
    private static async Task BothAsync(CorePeer first, CorePeer second, string status)
    {
        await CountAsync(first, status, 2).ConfigureAwait(true);
        await CountAsync(second, status, 2).ConfigureAwait(true);
    }
}
