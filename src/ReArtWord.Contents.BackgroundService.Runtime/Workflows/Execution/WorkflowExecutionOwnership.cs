using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>手動実行とSource入力に共通する、一件の実行と取消対象の寿命を管理します。</summary>
internal sealed class WorkflowExecutionOwnership(string peerId, Guid instanceId, TimeProvider clock, IWorkflowLog? log)
{
    private readonly ConcurrentDictionary<Guid, WorkflowExecutionCancellation> active = new();

    /// <summary>Progressを実行したPeerへ、元実行の終了を通知します。</summary>
    internal Func<Contracts.Management.Preview.ProgressSession, IReadOnlyList<Contracts.Cluster.PeerInfo>, CancellationToken, Task>? CompleteProgress
    {
        get; set;
    }

    /// <summary>一件の実行が終了したときに、予定の保持を解放します。</summary>
    internal Action<Guid>? Completed
    {
        get; set;
    }

    /// <summary>予定入力の実行結果を、元の保持記録へ返す処理。</summary>
    internal Func<ExecutionReport, CancellationToken, ValueTask>? ScheduledResult
    {
        get; set;
    }

    /// <summary>一件の開始・終了を共通Hookへ通知する先。</summary>
    internal IWorkflowEvents? Events
    {
        get; set;
    }

    /// <summary>元の実行要求へ取消を配送する処理です。</summary>
    internal Func<RemoteExecutionRequest, CancellationToken, Task<bool>>? CancelNode
    {
        get; set;
    }

    /// <summary>予定入力の取消を、担当交代後にも参照できる保存先へ伝えます。</summary>
    internal Func<Guid, CancellationToken, ValueTask<bool>>? CancelScheduledInput
    {
        get; set;
    }

    /// <summary>開始から完了まで、同じ入力IDを二重に実行しないよう管理します。</summary>
    /// <returns>元の一件の結果。</returns>
    // 抑制理由: Hookには不明結果を通知し、呼出元が持つ実行結果の保存・障害処理には元の例外を渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "観測通知の追加で元の実行例外を握りつぶさず、既存の受付結果保存とSource障害処理を維持するため。")]
    internal async Task<ExecutionReport> RunAsync(Guid id, string workflowId, string revision, Func<Task<ExecutionReport>> execute, IReadOnlyList<WorkflowHookStep>? hooks = null,
        Scheduling.ScheduledOccurrence? schedule = null, Guid? parentId = null, bool containsPersonalData = false, IReadOnlyList<SourceScope>? sourceScopes = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowRunStatus status = new(id, peerId, workflowId, revision, EOutcome.Unknown, true, OwnerInstanceId: instanceId);
        WorkflowExecutionCancellation? parent = null;
        if (parentId is { } owner)
        {
            parent = Find(owner);
        }
        DateTimeOffset started = clock.GetUtcNow();
        using WorkflowExecutionCancellation current = new(CancelNode, hooks, schedule, status)
        {
            Progress = parent?.Progress ?? new(new(id, peerId, instanceId, started), CompleteProgress),
            SourceScopes = sourceScopes ?? parent?.SourceScopes,
        };
        if (!active.TryAdd(id, current))
        {
            throw new InvalidOperationException("同じ入力IDの実行が既に進行中です。");
        }
        try
        {
            if (parent is not null && !parent.RegisterChild(id, current))
            {
                await current.CancelAsync(cancellationToken).ConfigureAwait(false);
            }
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(Guid.NewGuid(), "workflow.started", clock.GetUtcNow(), peerId,
                ContractJson.ToValue(new { id, workflowId, revision }), workflowId, CausationId: id, Hooks: hooks), CancellationToken.None).ConfigureAwait(false);
            ExecutionReport result = await execute().ConfigureAwait(false);
            await WorkflowFailureLog.WriteAsync(log, peerId, result, hooks, containsPersonalData: containsPersonalData, parentExecutionId: parentId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(result, schedule, CancellationToken.None).ConfigureAwait(false);
            await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(Guid.NewGuid(), "workflow.completed", clock.GetUtcNow(), peerId,
                ContractJson.ToValue(ExecutionReportPresentation.Bound(result)), workflowId, CausationId: id, Hooks: hooks), CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ExecutionReport unknown = new(id, workflowId, revision, EOutcome.Unknown, started, clock.GetUtcNow(), []);
            await WorkflowFailureLog.WriteAsync(log, peerId, unknown, hooks, error, containsPersonalData, parentId, CancellationToken.None).ConfigureAwait(false);
            ExecutionResultView view = ExecutionReportPresentation.Bound(unknown) with { DetailReason = "workflow.resultUnknown" };
            await ObserveAsync(unknown, schedule, CancellationToken.None).ConfigureAwait(false);
            await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(Guid.NewGuid(), "workflow.completed", clock.GetUtcNow(), peerId,
                ContractJson.ToValue(view), workflowId, CausationId: id, Hooks: hooks), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            parent?.CompleteChild(id);
            active.TryRemove(id, out _);
            Completed?.Invoke(id);
            if (parent is null)
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable progressLifetime = current.Progress.ConfigureAwait(false);
            }
        }
    }

    /// <summary>進行中の一件に属する取消状態を取得します。</summary>
    /// <returns>実行が終わっていればnull。</returns>
    internal WorkflowExecutionCancellation? Find(Guid id) => active.GetValueOrDefault(id);

    /// <summary>待受が終了していても、下流が実行中の一件を一覧へ含めます。</summary>
    /// <returns>現在この実体で追跡している有限実行。</returns>
    internal IReadOnlyList<WorkflowRunStatus> Active() => active.Values.Select(item => item.Status).ToArray();

    /// <summary>結果の保存通知が失敗しても、確認済みの実行結果を変更しません。</summary>
    /// <returns>予定入力の実行結果の通知試行。</returns>
    private async ValueTask ObserveAsync(ExecutionReport result, Scheduling.ScheduledOccurrence? schedule, CancellationToken cancellationToken = default)
    {
        if (schedule is null || ScheduledResult is null)
        {
            return;
        }
        try
        {
            await ScheduledResult(result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 保存先は未確認の記録を維持し、配送開始済みの回を再実行しません。
        }
    }

    /// <summary>元の一件の取消を要求し、別の入力へ波及させません。</summary>
    /// <returns>取消配送を確認できたか。元の実行結果は別途確認します。</returns>
    internal async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        WorkflowExecutionCancellation? execution = Find(id);
        if (execution is not null)
        {
            bool accepted = await execution.CancelAsync(cancellationToken).ConfigureAwait(false);
            if (execution.Schedule is not null && CancelScheduledInput is not null)
            {
                bool recorded = await CancelScheduledInput(id, cancellationToken).ConfigureAwait(false);
                return accepted && recorded;
            }
            return accepted;
        }
        return true;
    }
}
