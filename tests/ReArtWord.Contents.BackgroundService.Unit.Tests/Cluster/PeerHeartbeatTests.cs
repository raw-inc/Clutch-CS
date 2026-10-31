using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>保存領域の待機と生存通知を分離し、終了時に所有Taskを残さないことを検証します。</summary>
public sealed class PeerHeartbeatTests
{
    /// <summary>呼出元の同期コンテキストが停止していても、通知を独立した実行環境で継続します。</summary>
    /// <returns>呼出元のキューを進めずに確認できる生存通知です。</returns>
    [Fact]
    public async Task HeartbeatsDoNotDependOnTheCallersSynchronizationContext()
    {
        using CancellationTokenSource stopping = new();
        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SuspendedContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task heartbeat;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            heartbeat = PeerHeartbeat.RunAsync(TimeSpan.FromMilliseconds(10), TimeProvider.System, _ => Task.CompletedTask,
                _ =>
                {
                    published.TrySetResult();
                    return Task.CompletedTask;
                }, () =>
                {
                }, stopping.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(true);
            context.Release();
            await heartbeat.ConfigureAwait(true);
        }
    }

    /// <summary>状態確認が排他待ちでも送信を続け、同じ確認を並行起動しません。</summary>
    /// <returns>通知の継続と単一確認、停止時の完了待ちです。</returns>
    [Fact]
    public async Task BlockedRefreshDoesNotBlockHeartbeatsOrOverlapAndIsAwaitedAtShutdown()
    {
        using CancellationTokenSource stopping = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshes = 0, sends = 0, unavailable = 0;
        Task heartbeat = PeerHeartbeat.RunAsync(TimeSpan.FromMilliseconds(10), TimeProvider.System, async _ =>
        {
            Interlocked.Increment(ref refreshes);
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
        }, _ =>
        {
            if (3 <= Interlocked.Increment(ref sends))
            {
                published.TrySetResult();
            }
            return Task.CompletedTask;
        }, () => Interlocked.Increment(ref unavailable), stopping.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(1, Volatile.Read(ref refreshes));
            await stopping.CancelAsync().ConfigureAwait(true);
            Assert.False(heartbeat.IsCompleted);
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(true);
            release.TrySetResult();
            await heartbeat.ConfigureAwait(true);
        }
        Assert.True(0 < unavailable);
    }

    /// <summary>照会失敗を通知しても生存通知と次回確認を止めません。</summary>
    /// <returns>失敗後の再確認と継続通知です。</returns>
    [Fact]
    public async Task RefreshFailureMarksUnavailableAndCanRecoverOnTheNextTick()
    {
        using CancellationTokenSource stopping = new();
        TaskCompletionSource recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshes = 0, unavailable = 0, sends = 0;
        Task heartbeat = PeerHeartbeat.RunAsync(TimeSpan.FromMilliseconds(10), TimeProvider.System, _ =>
        {
            if (Interlocked.Increment(ref refreshes) == 1)
            {
                throw new IOException("Repository is temporarily unavailable.");
            }
            recovered.TrySetResult();
            return Task.CompletedTask;
        }, _ =>
        {
            if (2 <= Interlocked.Increment(ref sends))
            {
                published.TrySetResult();
            }
            return Task.CompletedTask;
        }, () => Interlocked.Increment(ref unavailable), stopping.Token);
        try
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.True(0 < Volatile.Read(ref unavailable));
            Assert.True(1 < Volatile.Read(ref sends));
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(true);
            await heartbeat.ConfigureAwait(true);
        }
    }

    /// <summary>試験側が解放するまで呼出元の作業を進めない同期コンテキストです。</summary>
    private sealed class SuspendedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();

        /// <summary>送信元の同期処理を、独立workerと取り違えず保留します。</summary>
        public override void Post(SendOrPostCallback d, object? state) => callbacks.Enqueue((d, state));

        /// <summary>失敗時にも保留中の所有タスクを終了可能にします。</summary>
        internal void Release()
        {
            while (callbacks.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
            {
                item.Callback(item.State);
            }
        }
    }
}
