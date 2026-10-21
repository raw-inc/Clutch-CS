using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;

/// <summary>通常処理を待たせず事象を配信し、容量超過による欠落を記録します。</summary>
public sealed class WorkflowEventHub(string? localPeerId = null, Guid? localInstanceId = null) : IWorkflowEvents
{
    private readonly ConcurrentDictionary<Guid, Channel<WorkflowEvent>> subscribers = new();
    private readonly Lock seenGate = new();
    private readonly HashSet<Guid> seen = [];
    private readonly Queue<Guid> order = new();
    private long published, dropped, omitted;
    private string? lastProblem;

    /// <summary>このPeerで発行した事象だけを、Peer間転送のキューへ渡します。</summary>
    internal event Action<WorkflowEvent, int>? LocalPublished;

    /// <summary>欠落を配信成功と区別する統計です。</summary>
    public WorkflowEventStatistics Statistics => new(subscribers.Count, Interlocked.Read(ref published), Interlocked.Read(ref dropped), Interlocked.Read(ref omitted), Volatile.Read(ref lastProblem));

    /// <summary>値を固定して購読先へ渡します。遅い購読先の処理完了は待ちません。</summary>
    /// <returns>今回の配信試行の完了。</returns>
    public ValueTask PublishAsync(WorkflowEvent item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        (WorkflowEvent copy, int bytes) = Bound(item with { PeerId = localPeerId ?? item.PeerId, PeerInstanceId = localInstanceId ?? item.PeerInstanceId });
        if (Deliver(copy))
        {
            LocalPublished?.Invoke(copy, bytes);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>発生元を検証済みの遠隔事象を取り込み、他Peerへ再転送しません。</summary>
    internal void Receive(WorkflowEvent item)
    {
        using (seenGate.EnterScope())
        {
            if (seen.Contains(item.Id))
            {
                return;
            }
        }
        Deliver(Bound(item).Item);
    }

    /// <summary>送信・受信経路の欠落を管理画面から確認できるようにします。</summary>
    internal void RecordDrop(string reason)
    {
        Interlocked.Increment(ref dropped);
        Volatile.Write(ref lastProblem, reason);
    }

    /// <summary>同じ事象の再到着を除き、各購読先の容量内へ通知します。</summary>
    /// <returns>初めて受け付けた有効な事象か。</returns>
    private bool Deliver(WorkflowEvent item)
    {
        if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) || 32 < (item.Hooks?.Count ?? 0)
            || item.DetailReason is not null && 32768 < JsonSize.Measure(item))
        {
            RecordDrop("event.invalid");
            return false;
        }
        using (seenGate.EnterScope())
        {
            if (!seen.Add(item.Id))
            {
                return false;
            }
            order.Enqueue(item.Id);
            while (4096 < order.Count)
            {
                seen.Remove(order.Dequeue());
            }
        }
        Interlocked.Increment(ref published);
        foreach (Channel<WorkflowEvent> subscriber in subscribers.Values)
        {
            if (!subscriber.Writer.TryWrite(item))
            {
                RecordDrop("event.subscriberFull");
            }
        }
        return true;
    }

    /// <summary>一件の保持量を制限し、省略した値を正常なnullとして扱わせません。</summary>
    /// <returns>元バッファから独立した事象。</returns>
    private (WorkflowEvent Item, int Bytes) Bound(WorkflowEvent item)
    {
        long bytes = JsonSize.Measure(item);
        if (32768 < bytes)
        {
            item = item with { Data = NodeValue.Null, DetailReason = "event.dataOmitted" };
            Interlocked.Increment(ref omitted);
            bytes = JsonSize.Measure(item);
        }
        return (item with { Data = item.Data.Copy(), Hooks = item.Hooks?.ToArray() }, checked((int)bytes));
    }

    /// <summary>購読の取消時に確実に配信先を取り除きます。</summary>
    /// <returns>列挙開始後に発生した事象です。</returns>
    public async IAsyncEnumerable<WorkflowEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        Channel<WorkflowEvent> channel = Channel.CreateBounded<WorkflowEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        subscribers[id] = channel;
        try
        {
            await foreach (WorkflowEvent item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}
