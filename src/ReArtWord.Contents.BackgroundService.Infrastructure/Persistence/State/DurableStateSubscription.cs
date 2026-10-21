using System.Threading.Channels;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>遅い待ち受けが保存を止めたり、変更値を無制限に保持したりしない購読です。</summary>
public sealed class DurableStateSubscription : IDisposable
{
    internal static long MaximumBytes => 8 * 1024 * 1024;
    private readonly DurableStateHub hub;
    private readonly Channel<DurableEntry> pending = Channel.CreateBounded<DurableEntry>(new BoundedChannelOptions(256) { AllowSynchronousContinuations = false, SingleReader = true });
    private DurableEntry? initial;
    private bool hasInitial, initialized, failed, disposed;
    private long bytes;

    /// <summary>保存先と一つのキーに購読を結び付けます。</summary>
    internal DurableStateSubscription(DurableStateHub hub, string address)
    {
        this.hub = hub;
        Address = address;
    }

    /// <summary>対象の一意な保存アドレスです。</summary>
    internal string Address
    {
        get;
    }

    /// <summary>購読開始後のDB読取りと競合したコミットを初期値へ含めます。</summary>
    /// <returns>初期読取り以降に確定した最新値、または読取り結果です。</returns>
    public DurableEntry? Initialize(DurableEntry? snapshot)
    {
        using (hub.Gate.EnterScope())
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                throw new InvalidOperationException("状態購読の初期値は一度だけ確定できます。");
            }
            initialized = true;
            DurableEntry? current = hasInitial ? initial
                                               : snapshot;
            initial = null;
            if (failed || MaximumBytes < current?.Value?.PayloadSize())
            {
                throw new IOException("State.ChangeValueTooLarge");
            }
            return current;
        }
    }

    /// <summary>通知待ちだけを行い、保存先を繰り返し読みません。</summary>
    /// <returns>次の確定変更です。上限超過は失敗として明示します。</returns>
    public async ValueTask<DurableEntry> ReadAsync(CancellationToken cancellationToken = default)
    {
        DurableEntry entry = await pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        using (hub.Gate.EnterScope())
        {
            bytes -= entry.Value?.PayloadSize() ?? 0;
        }
        return entry;
    }

    /// <summary>保存側で待たず、上限超過時はその購読だけを終了します。</summary>
    internal void Publish(DurableEntry entry, long size)
    {
        if (disposed || failed)
        {
            return;
        }
        if (!initialized && size <= MaximumBytes)
        {
            initial = entry;
            hasInitial = true;
            return;
        }
        if (MaximumBytes < size + bytes || !pending.Writer.TryWrite(entry))
        {
            failed = true;
            initial = null;
            pending.Writer.TryComplete(new IOException("State.ChangeQueueOverflow"));
            return;
        }
        bytes += size;
    }

    /// <summary>待機・保留値・保存先への参照を一度だけ解放します。</summary>
    public void Dispose()
    {
        using (hub.Gate.EnterScope())
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            initial = null;
            pending.Writer.TryComplete();
            while (pending.Reader.TryRead(out _))
            {
            }
            bytes = 0;
        }
        hub.Remove(this);
    }
}
