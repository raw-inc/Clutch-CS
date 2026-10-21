using System.Text.Json;
using System.Threading.Channels;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>バイト上限内のログを保持し、本来処理へ保存障害を伝播させません。</summary>
internal sealed class LogQueueProcessor
{
    private readonly Channel<LogWorkItem> queue = Channel.CreateUnbounded<LogWorkItem>();
    private readonly LogStore store;
    private readonly Func<LogPolicy> policy;
    private readonly Action<string?> failure;
    private long queued;
    private long droppedEntries;
    private long droppedBytes;

    /// <summary>保存先と動的バイト上限を接続します。</summary>
    internal LogQueueProcessor(LogStore store, Func<LogPolicy> policy, Action<string?> failure)
    {
        this.store = store;
        this.policy = policy;
        this.failure = failure;
    }

    /// <summary>保存待ちと再試行待ちを含むUTF8 バイト数です。</summary>
    internal long QueuedBytes => Interlocked.Read(ref queued);

    /// <summary>保存できなかった生成ログ数です。</summary>
    internal long DroppedEntries => Interlocked.Read(ref droppedEntries);

    /// <summary>把握できた欠落バイト数です。</summary>
    internal long DroppedBytes => Interlocked.Read(ref droppedBytes);

    /// <summary>バイト数を先に予約し、満杯でも本来処理を待たせません。</summary>
    /// <returns>受付できたかです。</returns>
    internal bool TryEnqueue(LogWorkItem item)
    {
        long current = Interlocked.Read(ref queued);
        while (item.Payload.Length <= policy().Options.QueueBytes - current)
        {
            long previous = Interlocked.CompareExchange(ref queued, current + item.Payload.Length, current);
            if (previous == current)
            {
                if (queue.Writer.TryWrite(item))
                {
                    return true;
                }
                Interlocked.Add(ref queued, -item.Payload.Length);
                break;
            }
            current = previous;
        }
        Drop(item.Payload.Length);
        item.Receipt?.TrySetResult(false);
        return false;
    }

    /// <summary>伏字不能または満杯による欠落を別表示へ反映します。</summary>
    internal void Drop(long bytes)
    {
        Interlocked.Increment(ref droppedEntries);
        Interlocked.Add(ref droppedBytes, bytes);
        failure("log.capture.incomplete");
    }

    /// <summary>受付済みの境界まで永続保存を待ちます。</summary>
    /// <returns>境界まで保存できたかです。</returns>
    internal async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(new(ReadOnlyMemory<byte>.Empty, false, receipt)))
        {
            return false;
        }
        return await receipt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存不能時は上限内の元キューを維持して回復を待ちます。</summary>
    /// <returns>キュー終了までの処理です。</returns>
    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        List<LogWorkItem> pending = [];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pending.Count == 0)
                {
                    if (!await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    while (pending.Count < 64 && queue.Reader.TryRead(out LogWorkItem? item))
                    {
                        pending.Add(item);
                    }
                }
                try
                {
                    LogWorkItem[] logs = pending.Where(item => !item.Payload.IsEmpty).ToArray();
                    await store.PersistAsync(logs, policy(), cancellationToken).ConfigureAwait(false);
                    foreach (LogWorkItem item in pending)
                    {
                        Interlocked.Add(ref queued, -item.Payload.Length);
                        item.Receipt?.TrySetResult(true);
                    }
                    pending.Clear();
                    failure(null);
                }
                catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
                {
                    failure("log.persistence.unavailable");
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (LogWorkItem item in pending)
            {
                Lose(item);
            }
            DiscardRemaining();
        }
    }

    /// <summary>worker未開始を含む終了時の残存キューを解放します。</summary>
    internal void DiscardRemaining()
    {
        while (queue.Reader.TryRead(out LogWorkItem? item))
        {
            Lose(item);
        }
    }

    /// <summary>新規受付を閉じ、正常時は既存をdrainします。</summary>
    internal void Complete()
    {
        queue.Writer.TryComplete();
    }

    /// <summary>終了時に未保存の一件を欠落として明示します。</summary>
    private void Lose(LogWorkItem item)
    {
        Interlocked.Add(ref queued, -item.Payload.Length);
        if (!item.Payload.IsEmpty)
        {
            Drop(item.Payload.Length);
        }
        item.Receipt?.TrySetResult(false);
    }
}
