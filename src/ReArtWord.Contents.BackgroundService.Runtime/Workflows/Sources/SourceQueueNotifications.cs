using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>入力到着と処理完了で受付を起こし、完了通知とタスク終了の競合を吸収します。</summary>
internal sealed class SourceQueueNotifications : IDisposable
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ConcurrentQueue<Guid> completions = new();

    /// <summary>多数の同時通知を一つにまとめ、先に届いた通知も失いません。</summary>
    internal void Wake()
    {
        using (gate.EnterScope())
        {
            if (wake.CurrentCount == 0)
            {
                wake.Release();
            }
        }
    }

    /// <summary>finally中の通知がタスクの完了より早く届いても、consumer側で完了まで確認します。</summary>
    internal void Complete(Guid id)
    {
        completions.Enqueue(id);
        Wake();
    }

    /// <summary>通知済みの実タスクだけを待ち、実行枠を解放します。</summary>
    /// <returns>届いている終了通知の処理完了です。</returns>
    internal async Task DrainAsync(ConcurrentDictionary<Guid, Task<EOutcome>> running, CancellationToken cancellationToken = default)
    {
        while (completions.TryDequeue(out Guid completed))
        {
            if (running.TryGetValue(completed, out Task<EOutcome>? task))
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                running.TryRemove(completed, out _);
            }
        }
    }

    /// <summary>通常は通知まで休止し、安全停止で保持している入力だけ従来の再確認周期を使います。</summary>
    /// <returns>次の到着・完了、または安全停止の再確認時点です。</returns>
    internal async Task WaitAsync(bool held, CancellationToken cancellationToken = default)
    {
        TimeSpan wait = Timeout.InfiniteTimeSpan;
        if (held)
        {
            wait = TimeSpan.FromMilliseconds(20);
        }
        _ = await wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通知元とconsumerが終了した後に待機資源を破棄します。</summary>
    public void Dispose() => wake.Dispose();
}
