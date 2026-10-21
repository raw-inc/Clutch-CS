using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別プロセスのCoreから実行し、共通枠と画面用の現在表示を実通信で確認します。</summary>
public sealed class ConcurrencyClusterTests
{
    /// <summary>異なるCoreで開始したWFの後続を直列化し、同じ待機をWFDashboardへ表示します。</summary>
    /// <returns>二つの実プロセスで共有した順序、入力値、最終解放です。</returns>
    [Fact]
    public async Task SeparateCoreProcessesShareAdmissionAndDashboardQueueAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(75));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(first, second, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition definition = Definition();
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => string.Equals((await second.GetAsync<JsonElement>("workflows.read", definition.Id, token).ConfigureAwait(true))
            .GetProperty("effectiveRevision").GetString(), saved.Revision, StringComparison.Ordinal), timeout.Token).ConfigureAwait(true);
        WorkflowRunRequest one = Request(saved.Revision!, "first"), two = Request(saved.Revision!, "second");
        WorkflowRunStatus started = await first.PostAsync<WorkflowRunStatus>("runs.start", one, timeout.Token).ConfigureAwait(true);
        Assert.True(started.Running);
        await CorePeer.WaitAsync(async token => (await ReadAsync(first, token).ConfigureAwait(true)).ActiveCount == 1, timeout.Token).ConfigureAwait(true);
        WorkflowRunStatus queued = await second.PostAsync<WorkflowRunStatus>("runs.start", two, timeout.Token).ConfigureAwait(true);
        Assert.True(queued.Running, queued.Reason);
        Assert.Equal("b", queued.PeerId);
        WorkflowConcurrencySnapshot? snapshot = null;
        await CorePeer.WaitAsync(async token =>
        {
            snapshot = await ReadAsync(second, token).ConfigureAwait(true);
            return snapshot.WaitingCount == 1;
        }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, snapshot!.ActiveCount);
        Assert.Equal("b", Assert.Single(snapshot.Waiting).OwnerPeerId);
        WfDashboardReference reference = new(definition.Id, "limit", ConcurrencyNodeTypes.Start);
        WfDashboardNodeState card = Assert.Single(await second.GetAsync<WfDashboardNodeState[]>("dashboards.nodes",
            new WfDashboardNodeQuery([reference]), timeout.Token).ConfigureAwait(true));
        Assert.Null(card.Problem);
        Assert.Equal(1, card.Concurrency?.WaitingCount);
        Assert.Equal(snapshot.Waiting[0].TicketId, Assert.Single(card.Concurrency!.Waiting).TicketId);
        WorkflowRunStatus completeOne = await CompletedAsync(first, one, timeout.Token).ConfigureAwait(true);
        WorkflowRunStatus completeTwo = await CompletedAsync(second, two, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, completeOne.Outcome);
        Assert.Equal(EOutcome.Succeeded, completeTwo.Outcome);
        Assert.Equal("first", completeOne.Report!.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result.Outputs["value"].GetString());
        Assert.Equal("second", completeTwo.Report!.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result.Outputs["value"].GetString());
        Assert.True(TimeSpan.FromMilliseconds(1800) <= completeTwo.Report.CompletedAt - completeOne.Report.CompletedAt);
        await CorePeer.WaitAsync(async token =>
        {
            WorkflowConcurrencySnapshot empty = await ReadAsync(first, token).ConfigureAwait(true);
            return empty.ActiveCount == 0 && empty.WaitingCount == 0;
        }, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>同じノードに異なる入力を持つ二つの実行を作ります。</summary>
    /// <returns>一回だけ開始する操作です。</returns>
    private static WorkflowRunRequest Request(string revision, string value) => new(Guid.NewGuid(), "cluster-limit", revision, "limit",
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) });

    /// <summary>実際の待機一覧APIを利用します。</summary>
    /// <returns>同じクラスタで観測した一つの区間です。</returns>
    private static async Task<WorkflowConcurrencySnapshot> ReadAsync(CorePeer peer, CancellationToken cancellationToken = default)
        => Assert.Single(await peer.GetAsync<WorkflowConcurrencySnapshot[]>("workflows.concurrency", new[] { new WorkflowConcurrencyTarget("cluster-limit", "limit") }, cancellationToken).ConfigureAwait(true));

    /// <summary>受付応答と実行完了を区別します。</summary>
    /// <returns>元の所有Coreが確認した終了結果です。</returns>
    private static async Task<WorkflowRunStatus> CompletedAsync(CorePeer peer, WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        WorkflowRunStatus? current = null;
        await CorePeer.WaitAsync(async token =>
        {
            current = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !current.Running;
        }, cancellationToken).ConfigureAwait(true);
        return current!;
    }

    /// <summary>限定した区間の副作用を別Coreへ配送します。</summary>
    /// <returns>本番の待受・外部APIを使用しないWFです。</returns>
    private static WorkflowDefinition Definition()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        return new("cluster-limit", "Cluster concurrency", [new("limit", ConcurrencyNodeTypes.Start, JsonSerializer.SerializeToElement(new { maxConcurrency = 1, waitTimeoutMs = 10000 })),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 2500 }), Target: new(EExecutionScope.One, ["b"])),
            new("end", ConcurrencyNodeTypes.End, empty)], [new("limit", "scope", "end", "scope"), new("limit", "value", "delay", "value"), new("delay", "value", "end", "value")]);
    }

    /// <summary>保存版を共有する二つのPeerの通常参加を待ちます。</summary>
    /// <returns>各Coreが互いをReadyとして認識した状態です。</returns>
    private static Task ReadyAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        JsonElement left = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        JsonElement right = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        return left.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
            && right.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
    }, cancellationToken);
}
