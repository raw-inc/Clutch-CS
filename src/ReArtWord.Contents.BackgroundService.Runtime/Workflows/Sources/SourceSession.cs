using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>一つの待受の停止通知と追跡可能な実タスクを保持します。</summary>
internal sealed class SourceSession : IDisposable
{
    private SourceExecutionInfo info;

    /// <summary>固定した開始要求から待受を作成します。</summary>
    internal SourceSession(RemoteExecutionRequest request, DateTimeOffset startedAt, bool resettable = false)
    {
        Request = request;
        info = new SourceExecutionInfo(request.Id, request.WorkflowId, request.Node.Id, request.TargetPeerId, request.Revision, startedAt, new NodeSourceStatus(ESourceState.Starting), PeerInstanceId: request.TargetInstanceId);
        if (resettable)
        {
            Signals = System.Threading.Channels.Channel.CreateBounded<long>(new System.Threading.Channels.BoundedChannelOptions(1)
            {
                SingleReader = true, SingleWriter = false, FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        }
    }

    /// <summary>最新の生存信号の時刻だけを保持し、本文や無制限の通知列は蓄積しません。</summary>
    internal System.Threading.Channels.Channel<long>? Signals
    {
        get;
    }

    /// <summary>接続開始時の不変入力です。</summary>
    internal RemoteExecutionRequest Request
    {
        get;
    }

    /// <summary>新規入力の受付を正常に終了させます。</summary>
    internal CancellationTokenSource Stop
    {
        get;
    } = new();

    /// <summary>進行中の処理も取り消す要求です。</summary>
    internal CancellationTokenSource Cancel
    {
        get;
    } = new();

    /// <summary>最初の待受状態または終了までを通知します。</summary>
    internal TaskCompletionSource<NodeSourceStatus> Started
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>待受の実際の終了を確認するタスクです。</summary>
    internal Task Completion
    {
        get; set;
    } = Task.CompletedTask;

    /// <summary>設定・所属変更後に同じ定義の自動待受を再開する必要があるかを示します。</summary>
    internal bool RestartRequested
    {
        get; set;
    }

    /// <summary>人による停止は、後から到着する所属変更で自動再開へ読み替えません。</summary>
    internal bool HumanStopped
    {
        get; set;
    }

    /// <summary>現在の表示用状態です。</summary>
    internal SourceExecutionInfo Info => Volatile.Read(ref info);

    /// <summary>終了中の待受を復活させず、最新の通知時刻だけを更新します。</summary>
    /// <returns>既存の待受が通知を受け付けたかです。</returns>
    internal bool TrySignal(IReadOnlySet<string> inputs, RemoteExecutionRequest request, long timestamp)
        => inputs.Any(request.Inputs.ContainsKey) && !Stop.IsCancellationRequested && !Cancel.IsCancellationRequested
            && Signals?.Writer.TryWrite(timestamp) == true;

    /// <summary>所有範囲の終了を手動停止と同じ再開不可の状態へ反映します。</summary>
    /// <returns>この待受が終了範囲に含まれていたかです。</returns>
    internal async ValueTask<bool> StopIfScopeEndedAsync(IReadOnlySet<SourceScope> ended, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Request.SourceScopes?.Any(ended.Contains) != true)
        {
            return false;
        }
        HumanStopped = true;
        RestartRequested = false;
        await Stop.CancelAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>接続設定が同じなら待受を維持して表示版を更新し、変更時だけ正常停止を要求します。</summary>
    /// <returns>新しい有効版に対応する待受寿命の調整です。</returns>
    internal Task ApplyAsync(CompiledWorkflow plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NodeDefinition? next = plan.FindNode(Request.Node.Id);
        if (!plan.Definition.Enabled || plan.DependencyIssues.Count != 0 || SubworkflowInterface.IsSubworkflow(plan.Definition) || next is null
            || !string.Equals(SourceSelection.ConnectionVersion(next), SourceSelection.ConnectionVersion(Request.Node), StringComparison.Ordinal))
        {
            RestartRequested = true;
            return Stop.CancelAsync();
        }
        if (!Completion.IsCompleted && !Stop.IsCancellationRequested && !Cancel.IsCancellationRequested)
        {
            Update(revision: plan.Revision);
        }
        return Task.CompletedTask;
    }

    /// <summary>終了後の表示情報だけを件数制限付きで残します。</summary>
    internal void ArchiveTo(System.Collections.Concurrent.ConcurrentQueue<SourceExecutionInfo> history)
    {
        history.Enqueue(Info);
        while (100 < history.Count)
        {
            history.TryDequeue(out _);
        }
    }

    /// <summary>稼働状態と設定版の同時更新を失わず、不変の状態を原子的に置き換えます。</summary>
    internal void Update(NodeSourceStatus? status = null, string? revision = null, NodeResult? result = null)
    {
        SourceExecutionInfo current, next;
        do
        {
            current = Info;
            next = current with { Status = status ?? current.Status, Revision = revision ?? current.Revision, Result = result ?? current.Result };
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref info, next, current), current));
        if (status is not null)
        {
            Started.TrySetResult(status);
        }
    }

    /// <summary>元の受付済み入力と待受の終了を確認し、確認待ちの取消を実停止と区別します。</summary>
    /// <returns>この待受の終了結果。停止を確認できなければUnknown。</returns>
    internal async Task<NodeResult> WaitForStopAsync(SourceEmissionQueue queue, bool cancel, bool waitForCompletion, Scheduling.ISourceScheduleStore? schedules, CancellationToken cancellationToken = default)
    {
        if (!waitForCompletion && !cancel)
        {
            return NodeResult.Success(NodeValue.FromObject(new { sourceId = Request.Id, stopRequested = true }));
        }
        try
        {
            bool confirmed = !cancel || await queue.CancelSessionAsync(Request.Id, cancellationToken).ConfigureAwait(false) != EOutcome.Unknown;
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (cancel && schedules is not null)
            {
                confirmed &= await schedules.CancelAsync(Request, cancellationToken).ConfigureAwait(false);
            }
            if (!confirmed)
            {
                return NodeResult.Failure("source.inputCancelUnconfirmed", "元Source入力の停止結果を確認できません。元の一件結果を照会してください。", EOutcome.Unknown);
            }
            return Info.Result ?? NodeResult.Failure("source.unconfirmed", "終了結果を確認できません。", EOutcome.Unknown);
        }
        catch (OperationCanceledException)
        {
            return NodeResult.Failure("source.stopUnconfirmed", "停止の確認待ちを打ち切りました。", EOutcome.Unknown);
        }
    }

    /// <summary>完了済みの待受の通知資源を解放します。</summary>
    public void Dispose()
    {
        Signals?.Writer.TryComplete();
        Stop.Dispose();
        Cancel.Dispose();
    }
}
