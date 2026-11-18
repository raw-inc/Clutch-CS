using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;

/// <summary>一つの制限ノードの現在値と待機順だけを保持し、待機中の保存を発生させません。</summary>
internal sealed class ConcurrencyGateQueue(Action<ConcurrencyGateQueue> returned) : IDisposable
{
    /// <summary>このノードの取得と解放だけを直列化します。</summary>
    internal SemaphoreSlim Changes
    {
        get;
    } = new(1, 1);

    /// <summary>現在の占有記録です。履歴全体を保持しません。</summary>
    internal DurableEntry? Entry
    {
        get; set;
    }

    /// <summary>現在値を読み取った担当世代です。</summary>
    internal long Epoch
    {
        get; set;
    } = -1;

    /// <summary>確認済みの現役枠です。</summary>
    internal List<ConcurrencyPermit> Active
    {
        get;
    } = [];

    /// <summary>同じ受付を重複しない待機一覧です。</summary>
    internal LinkedList<WorkflowConcurrencyWaiter> Waiting
    {
        get;
    } = [];

    /// <summary>取消対象を受付順のリストから定数時間で取り除く索引です。</summary>
    internal Dictionary<Guid, LinkedListNode<WorkflowConcurrencyWaiter>> Waiters
    {
        get;
    } = [];

    /// <summary>待機・参照中の呼出し数です。担当側の集合ロック内だけで変更します。</summary>
    internal int Users
    {
        get; set;
    }

    /// <summary>一つの到達を受付順に追加します。</summary>
    internal void Enqueue(WorkflowConcurrencyWaiter waiter)
    {
        if (!Waiters.ContainsKey(waiter.TicketId))
        {
            Waiters.Add(waiter.TicketId, Waiting.AddLast(waiter));
        }
    }

    /// <summary>枠取得や取消が確定した到達を待機表示から除きます。</summary>
    internal void Remove(Guid ticket)
    {
        if (Waiters.Remove(ticket, out LinkedListNode<WorkflowConcurrencyWaiter>? node))
        {
            Waiting.Remove(node);
            Pulse();
        }
    }

    /// <summary>現役枠の所有者を最後に照会した単調時計の位置です。日時補正で照会間隔を変えません。</summary>
    internal long? RecoveryChecked
    {
        get; set;
    }

    /// <summary>ノード全体の所有確認は5秒おきに行います。呼出側はChangesの排他を保持します。</summary>
    /// <returns>今回、現役枠の所有確認も行う場合はtrueです。</returns>
    internal bool BeginRecovery(TimeProvider clock)
    {
        if (RecoveryChecked is not null && clock.GetElapsedTime(RecoveryChecked.Value) < TimeSpan.FromSeconds(5))
        {
            return false;
        }
        RecoveryChecked = clock.GetTimestamp();
        return true;
    }

    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>排他内で取得し、解放通知を取り逃さずに待つ世代です。</summary>
    internal Task Changed => changed.Task;

    /// <summary>枠の解放や先頭の取消で待機を再評価させます。</summary>
    internal void Pulse()
    {
        TaskCompletionSource previous = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>この呼出しが借用した参照を担当へ返します。</summary>
    public void Dispose() => returned(this);

    /// <summary>最後の参照が戻った後、担当だけが排他の所有を終了します。</summary>
    internal void Close()
    {
        using SemaphoreSlim completed = Changes;
    }
}
