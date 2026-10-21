using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>一件のワークフローから配送した実要求だけを明示取消の対象にします。</summary>
internal sealed class WorkflowExecutionCancellation(Func<RemoteExecutionRequest, CancellationToken, Task<bool>>? cancel, IReadOnlyList<Contracts.Observability.WorkflowHookStep>? hooks,
    Scheduling.ScheduledOccurrence? schedule, WorkflowRunStatus status) : IDisposable
{
    /// <summary>内部の子発火と共有する、元WF一回の進捗の所有者です。</summary>
    internal WorkflowProgressLifetime? Progress
    {
        get; init;
    }

    /// <summary>子発火にも引き継ぐ、後続待受の所有範囲です。</summary>
    internal IReadOnlyList<SourceScope>? SourceScopes
    {
        get; init;
    }

    /// <summary>この実体が実行中の一件を照会する情報。</summary>
    internal WorkflowRunStatus Status
    {
        get;
    } = status;

    /// <summary>同じ入力から派生する遠隔処理へ引き継ぐHookの通過履歴。</summary>
    internal IReadOnlyList<Contracts.Observability.WorkflowHookStep>? Hooks
    {
        get;
    } = hooks;

    /// <summary>この入力に固定された未開始対象の待機方針。</summary>
    internal Scheduling.ScheduledOccurrence? Schedule
    {
        get;
    } = schedule;

    private readonly Lock gate = new();
    private readonly CancellationTokenSource interruption = new();
    private bool disposed;

    private readonly Dictionary<Guid, RemoteExecutionRequest> active = [];
    private readonly Dictionary<Guid, WorkflowExecutionCancellation> children = [];
    private bool cancelled;
    private bool skipped;

    /// <summary>ノード本体以外の保存・値取得待ちにも実行単位の取消を伝えます。</summary>
    internal CancellationToken Token => interruption.Token;

    /// <summary>反復の未開始要素にも、元の一件に対する明示取消を適用します。</summary>
    internal bool IsCancellationRequested
    {
        get
        {
            using (gate.EnterScope())
            {
                return cancelled;
            }
        }
    }

    /// <summary>明示取消により開始できなかった処理を成功へ集約しません。</summary>
    /// <returns>取消による未実行が含まれるか。</returns>
    internal bool PreventedExecution(IEnumerable<ExecutionFrame> frames)
    {
        using (gate.EnterScope())
        {
            if (!cancelled)
            {
                return false;
            }
            return skipped || frames.SelectMany(static frame => frame.Targets).Any(target => ExecutionOutcomes.CancelledBeforeStart(target.Result));
        }
    }

    /// <summary>配送前に元の実体と要求IDを固定し、取消後の新規配送を拒否します。</summary>
    /// <returns>この一回の配送を開始できるか。</returns>
    internal bool Register(RemoteExecutionRequest request)
    {
        using (gate.EnterScope())
        {
            if (cancelled)
            {
                skipped = true;
                return false;
            }
            active.Add(request.Id, request);
            return true;
        }
    }

    /// <summary>完了確認した要求を取消対象から外します。</summary>
    internal void Complete(Guid id)
    {
        using (gate.EnterScope())
        {
            active.Remove(id);
        }
    }

    /// <summary>合流から開始する子実行を、元入力の明示取消へ接続します。</summary>
    /// <returns>既に取消済みの場合はfalseで、子側も開始前に取消します。</returns>
    internal bool RegisterChild(Guid id, WorkflowExecutionCancellation child)
    {
        using (gate.EnterScope())
        {
            if (cancelled)
            {
                skipped = true;
                return false;
            }
            children.Add(id, child);
            return true;
        }
    }

    /// <summary>終了済みの子実行を保持し続けません。</summary>
    internal void CompleteChild(Guid id)
    {
        using (gate.EnterScope())
        {
            children.Remove(id);
        }
    }

    /// <summary>接続の切断とは別に、元の開始済み実体へ明示取消を配送します。</summary>
    /// <returns>取消要求を配送できたか。実停止を保証しません。</returns>
    internal async Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        RemoteExecutionRequest[] requests;
        WorkflowExecutionCancellation[] descendants;
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return true;
            }
            cancelled = true;
            requests = active.Values.ToArray();
            descendants = children.Values.ToArray();
        }
        IEnumerable<Task<bool>> deliveries = descendants.Select(child => child.CancelAsync(cancellationToken));
        if (cancel is not null)
        {
            deliveries = deliveries.Concat(requests.Select(request => SendAsync(request, cancellationToken)));
        }
        await InterruptAsync(CancellationToken.None).ConfigureAwait(false);
        bool[] accepted = await Task.WhenAll(deliveries).ConfigureAwait(false);
        return (requests.Length == 0 || cancel is not null) && accepted.All(static value => value);
    }

    /// <summary>破棄と競合せず取消を開始し、コールバックはロック外で待ちます。</summary>
    /// <returns>実行単位の取消通知です。</returns>
    private Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return Task.CompletedTask;
            }
            return interruption.CancelAsync();
        }
    }

    /// <summary>取消経路の失敗を元処理の停止成功へ変えません。</summary>
    /// <returns>確認できた取消受付です。</returns>
    private async Task<bool> SendAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await cancel!(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>実行の所有期間が終わった取消資源を解放します。</summary>
    public void Dispose()
    {
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            interruption.Dispose();
        }
    }
}
