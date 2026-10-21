using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>手動実行の永続受付と実タスクを追跡し、確認不能な処理を再実行しません。</summary>
internal sealed class WorkflowRunService : IAsyncDisposable
{
    private readonly ServiceRuntime runtime;
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim admission = new(1, 1);
    private static string Scope => "manual-runs";
    private string LocalScope => "peer:" + runtime.Cluster.LocalPeerId + ":manual-runs";

    /// <summary>管理APIと同じランタイムへ接続します。</summary>
    public WorkflowRunService(ServiceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        this.runtime = runtime;
        Cancellations = new(runtime.Repository, runtime.Cluster, ReadAsync, runtime.Engine.RequestCancellationAsync);
    }

    /// <summary>元の実行実体を指定した取消と、その操作結果の照会を提供します。</summary>
    internal WorkflowRunCancellation Cancellations
    {
        get;
    }

    /// <summary>開始前の記録を共有してから一度だけタスクを開始します。</summary>
    /// <returns>受付または既存操作の確認結果です。</returns>
    public async Task<WorkflowRunStatus> StartAsync(WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            admission.Release();
        }
    }

    /// <summary>同一Peerへの同時再送を直列化し、記録済み要求を再起動しません。</summary>
    /// <returns>受付と再照会の結果です。</returns>
    // 抑制理由: 実行はAPI応答より長いため、runningで所有し、照会時と終了時にawaitします。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "開始したタスクはrunningに保存し、ReadAsyncまたはDisposeAsyncで終了をawaitします。")]
    private async Task<WorkflowRunStatus> StartCoreAsync(WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (KeyValuePair<Guid, Task> completed in running.Where(static item => item.Value.IsCompleted))
        {
            await completed.Value.ConfigureAwait(false);
            running.TryRemove(completed.Key, out _);
        }
        request = ContractJson.Copy(request);
        string fingerprint = ContractJson.Hash(request);
        string key = request.OperationId.ToString("N");
        DurableEntry? existing = await ReadRecordAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing?.Value is not null)
        {
            RunRecord previous = existing.Value.Json.Deserialize<RunRecord>() ?? throw new InvalidDataException("手動実行記録が不正です。");
            if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Refuse(request, "run.requestMismatch");
            }
            return await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false) ?? previous.Status;
        }
        CompiledWorkflow? plan = runtime.Sources.GetEffective(request.WorkflowId);
        if (plan?.Definition.IsDashboard == true)
        {
            return Refuse(request, "run.dashboardNotExecutable");
        }
        if (plan is null || (request.Revision is not null && !string.Equals(plan.Revision, request.Revision, StringComparison.Ordinal))
            || (request.EntryNodeId is not null && !plan.Incoming.ContainsKey(request.EntryNodeId)))
        {
            return Refuse(request, "run.revisionOrEntryMissing");
        }
        if (!PeerStatusRules.CanParticipate(runtime.Status) && runtime.Status != EPeerStatus.Detached)
        {
            return Refuse(request, "run.peerNotReady");
        }
        WorkflowRunStatus status = new(request.OperationId, runtime.Cluster.LocalPeerId, request.WorkflowId, plan.Revision, EOutcome.Unknown, false, Reason: "run.started", OwnerInstanceId: runtime.Cluster.LocalInstanceId);
        RunRecord record = new(fingerprint, status);
        string scope = Scope;
        bool local = runtime.Status == EPeerStatus.Detached;
        if (local)
        {
            scope = LocalScope;
        }
        DurableReceipt receipt = await SaveAsync(request.OperationId, [new DurableMutation(scope, key, null, NodeValue.FromObject(record))], local, cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            return status with { Outcome = receipt.Outcome, Reason = receipt.Reason ?? "run.admissionUnconfirmed" };
        }
        running[request.OperationId] = ExecuteAsync(plan, request, fingerprint, receipt.Entries[0].Version, scope, local, stopping.Token);
        return status with { Running = true };
    }

    /// <summary>開始済み操作を再実行せず照会します。</summary>
    /// <returns>記録された確認結果、または未受付のnullです。</returns>
    public async Task<WorkflowRunStatus?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await ReadRecordAsync(operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        RunRecord? record = entry?.Value?.Json.Deserialize<RunRecord>();
        if (record is null)
        {
            WorkflowRunStatus? scheduled = await runtime.ScheduledInputs.ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (scheduled is not null)
            {
                return scheduled;
            }
            WorkflowRunStatus? active = runtime.Engine.Active().FirstOrDefault(item => item.OperationId == operationId);
            if (active is not null)
            {
                return active;
            }
            ExecutionResultView? completed = runtime.Engine.RecentViews().FirstOrDefault(item => (item.Report?.Id ?? item.Summary?.Id) == operationId);
            if (completed is null)
            {
                return null;
            }
            return new(operationId, runtime.Cluster.LocalPeerId, completed.Report?.WorkflowId ?? completed.Summary!.WorkflowId,
                completed.Report?.Revision ?? completed.Summary!.Revision, completed.Report?.Outcome ?? completed.Summary!.Outcome, false,
                completed.Report, Summary: completed.Summary, DetailReason: completed.DetailReason, OwnerInstanceId: runtime.Cluster.LocalInstanceId);
        }
        if (running.TryGetValue(operationId, out Task? task))
        {
            if (!task.IsCompleted)
            {
                return record.Status with { Running = true };
            }
            await task.ConfigureAwait(false);
            running.TryRemove(operationId, out _);
        }
        ExecutionReport? actual = runtime.Engine.Find(operationId);
        if (actual is not null)
        {
            ExecutionResultView view = ExecutionReportPresentation.Bound(actual);
            return record.Status with { Outcome = actual.Outcome, Running = false, Report = view.Report, Summary = view.Summary, DetailReason = view.DetailReason, Reason = null };
        }
        return record.Status with { Running = false, DetailsUnavailable = record.Status.Summary is not null && record.Status.Report is null };
    }

    /// <summary>このPeerで実行中の処理を、完了結果の大きな出力を含めずに取得します。</summary>
    /// <returns>実行中の処理と次ページの位置。</returns>
    internal async Task<ActiveRunsPage> ActiveAsync(ActiveRunsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 100)
        {
            throw new ArgumentException("実行一覧の件数は1から100を指定してください。", nameof(query));
        }
        Guid[] ids = running.Where(item => !item.Value.IsCompleted).Select(static item => item.Key)
            .Concat(runtime.Engine.Active().Select(item => item.OperationId)).Distinct()
            .Where(id => query.After is null || 0 < id.CompareTo(query.After.Value)).Order().Take(query.Limit + 1).ToArray();
        List<WorkflowRunStatus> items = [];
        foreach (Guid id in ids.Take(query.Limit))
        {
            WorkflowRunStatus? status = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
            if (status?.Running == true)
            {
                items.Add(status with { Report = null, Summary = null });
            }
        }
        Guid? next = null;
        if (query.Limit < ids.Length)
        {
            next = ids[query.Limit - 1];
        }
        return new(items, next);
    }

    /// <summary>呼出元の接続が終わっても実処理を追跡します。</summary>
    /// <returns>実処理と確認結果の保存です。</returns>
    // 抑制理由: 実行タスクの境界で確認可能な不明結果を保存し、未観測の例外を残しません。
    [SuppressMessage("Design", "CA1031", Justification = "管理実行の障害を実行済みか不明の結果として保持します。")]
    private async Task ExecuteAsync(CompiledWorkflow plan, WorkflowRunRequest request, string fingerprint, string version, string scope, bool local, CancellationToken cancellationToken = default)
    {
        WorkflowRunStatus result;
        try
        {
            ExecutionReport report = await runtime.Engine.ExecuteAsync(plan, request.EntryNodeId, request.Inputs, request.OperationId, cancellationToken).ConfigureAwait(false);
            ExecutionResultView view = ExecutionReportPresentation.Bound(report);
            result = new WorkflowRunStatus(request.OperationId, runtime.Cluster.LocalPeerId, request.WorkflowId, plan.Revision, report.Outcome, false,
                view.Report, Summary: view.Summary, DetailReason: view.DetailReason, OwnerInstanceId: runtime.Cluster.LocalInstanceId);
        }
        catch (Exception)
        {
            result = new WorkflowRunStatus(request.OperationId, runtime.Cluster.LocalPeerId, request.WorkflowId, plan.Revision, EOutcome.Unknown, false, Reason: "run.executionUnconfirmed", OwnerInstanceId: runtime.Cluster.LocalInstanceId);
        }
        try
        {
            _ = await SaveAsync(Guid.NewGuid(), [new DurableMutation(scope, request.OperationId.ToString("N"), version, NodeValue.FromObject(new RunRecord(fingerprint, result)))], local, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 開始記録はUnknownのまま残り、暗黙の再実行を抑止します。ローカル実行結果はEngineでも照会できます。
        }
    }

    /// <summary>明示離脱時のローカル実行だけをローカル記録へ限定します。</summary>
    /// <returns>同じ操作の永続結果です。</returns>
    private ValueTask<DurableReceipt> SaveAsync(Guid id, IReadOnlyList<DurableMutation> changes, bool local, CancellationToken cancellationToken = default)
    {
        if (local)
        {
            return runtime.Repository.ApplyBatchAsync(id, changes, cancellationToken);
        }
        return runtime.Cluster.ReplicateAsync(id, changes, cancellationToken);
    }

    /// <summary>離脱中のローカル記録を再参加後も識別し、共有へ混ぜません。</summary>
    /// <returns>既存の実行記録です。</returns>
    private async ValueTask<DurableEntry?> ReadRecordAsync(string key, CancellationToken cancellationToken = default)
    {
        return await runtime.Repository.ReadAsync(Scope, key, cancellationToken).ConfigureAwait(false)
            ?? await runtime.Repository.ReadAsync(LocalScope, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>副作用を開始しない理由を返します。</summary>
    /// <returns>未実行結果です。</returns>
    private WorkflowRunStatus Refuse(WorkflowRunRequest request, string reason) => new(request.OperationId, runtime.Cluster.LocalPeerId, request.WorkflowId, request.Revision, EOutcome.NotExecuted, false, Reason: reason);

    /// <summary>所有する実行へ取消を通知し、確認の終了を待ちます。</summary>
    /// <returns>追跡タスクの終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(running.Values).ConfigureAwait(false);
        stopping.Dispose();
        admission.Dispose();
        Cancellations.Dispose();
    }

    /// <summary>要求同一性と再照会可能な実行結果を保持します。</summary>
    /// <param name="Fingerprint">要求内容のハッシュ。</param>
    /// <param name="Status">実行結果。</param>
    private sealed record RunRecord(string Fingerprint, WorkflowRunStatus Status);
}
