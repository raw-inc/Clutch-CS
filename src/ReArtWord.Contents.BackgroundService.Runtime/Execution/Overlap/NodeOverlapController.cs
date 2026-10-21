using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Execution.Overlap;

/// <summary>実行Peerごとに同一ノードの重複受付を制御します。</summary>
public sealed class NodeOverlapController
{
    private readonly Lock sync = new();
    private readonly Dictionary<(string Workflow, string Node, string Peer), Slot> slots = [];
    private readonly int queueCapacity;

    /// <summary>ノードとPeerごとの保留上限を指定します。</summary>
    public NodeOverlapController(int queueCapacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        this.queueCapacity = queueCapacity;
    }

    /// <summary>受付時に固定した入力と設定の処理を、明示方針で開始します。</summary>
    /// <returns>実処理の結果、または開始しなかった理由です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(string workflowId, string nodeId, string peerId,
        EOverlapPolicy policy, Func<CancellationToken, ValueTask<NodeResult>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(execute);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Refused("overlap.cancelled");
        }
        (string Workflow, string Node, string Peer) key = (workflowId, nodeId, peerId);
        Pending pending = new();
        Slot slot;
        using (sync.EnterScope())
        {
            if (!slots.TryGetValue(key, out slot!))
            {
                slot = new();
                slots.Add(key, slot);
            }
            if (policy == EOverlapPolicy.Parallel || slot.Active == 0)
            {
                slot.Active++;
                pending.Ready.SetResult(null);
            }
            else
            {
                if (policy == EOverlapPolicy.Skip)
                {
                    return Refused("overlap.running");
                }
                if (policy == EOverlapPolicy.Latest)
                {
                    foreach (Pending previous in slot.Waiting)
                    {
                        previous.Link = null;
                        previous.Ready.SetResult(Refused("overlap.replaced"));
                    }
                    slot.Waiting.Clear();
                }
                if (queueCapacity <= slot.Waiting.Count)
                {
                    return Refused("overlap.capacity");
                }
                pending.Link = slot.Waiting.AddLast(pending);
            }
        }
        using CancellationTokenRegistration registration = cancellationToken.Register(() => CancelPending(pending));
        NodeResult? refusal = await pending.Ready.Task.ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Refused("overlap.cancelled");
            }
            return await execute(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Complete(key, slot);
        }
    }

    /// <summary>開始前の保留だけを取り消します。</summary>
    private void CancelPending(Pending pending)
    {
        using Lock.Scope scope = sync.EnterScope();
        if (pending.Link is not null)
        {
            pending.Link.List!.Remove(pending.Link);
            pending.Link = null;
            pending.Ready.SetResult(Refused("overlap.cancelled"));
        }
    }

    /// <summary>前回の完了後に次の保留を一件だけ開始します。</summary>
    private void Complete((string Workflow, string Node, string Peer) key, Slot slot)
    {
        using Lock.Scope scope = sync.EnterScope();
        slot.Active--;
        if (slot.Active == 0)
        {
            if (slot.Waiting.First is { } first)
            {
                slot.Waiting.RemoveFirst();
                first.Value.Link = null;
                slot.Active++;
                first.Value.Ready.SetResult(null);
            }
            else
            {
                slots.Remove(key);
            }
        }
    }

    /// <summary>未実行の安定した理由を作成します。</summary>
    /// <returns>副作用を開始していない結果です。</returns>
    private static NodeResult Refused(string reason)
    {
        return NodeResult.Failure(reason, "重複実行方針により開始しませんでした。", EOutcome.NotExecuted);
    }

    /// <summary>一つの実行場所の処理数と保留です。</summary>
    private sealed record Slot
    {
        internal int Active
        {
            get; set;
        }

        internal LinkedList<Pending> Waiting
        {
            get;
        } = new();
    }

    /// <summary>呼出元自身が待機する受付です。</summary>
    private sealed record Pending
    {
        internal TaskCompletionSource<NodeResult?> Ready
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal LinkedListNode<Pending>? Link
        {
            get; set;
        }
    }
}
