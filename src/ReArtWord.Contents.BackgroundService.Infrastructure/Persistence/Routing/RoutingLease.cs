namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>同じrouting領域の別実体・別プロセスへ途中状態を公開しない排他です。</summary>
internal sealed class RoutingLease : IDisposable
{
    private static readonly Lock gate = new();
    private static readonly Dictionary<string, Pending> waiting = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase
                                                                                                  : StringComparer.Ordinal);
    private readonly FileStream file;
    private readonly string path;
    private readonly Pending pending;
    private int disposed;

    /// <summary>ファイル排他とプロセス内の取得順を同じ寿命で所有します。</summary>
    private RoutingLease(FileStream file, string path, Pending pending)
    {
        this.file = file;
        this.path = path;
        this.pending = pending;
    }

    /// <summary>ローカルファイルを排他的に開き、取消可能な有限待機で取得します。</summary>
    /// <returns>呼出側が破棄して排他を解除するストリームです。</returns>
    // 抑制理由: 成功時はleaseへ所有権を渡し、取得失敗時はfinallyのusingで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "ファクトリーの戻り値へ所有権を移し、失敗経路では解放します。専用usingは成功した排他を返す前に破棄するため使用しません。 ")]
    internal static async Task<RoutingLease> AcquireAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        Pending pending;
        using (gate.EnterScope())
        {
            if (!waiting.TryGetValue(path, out pending!))
            {
                pending = new();
                waiting.Add(path, pending);
            }
            pending.Users++;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        bool entered = false, transferred = false;
        FileStream? file = null;
        OperationCanceledException? expired = null;
        try
        {
            // 同一プロセスの全待機者が20msごとにCreateFileを競合させることを防ぎます。
            await pending.Queue.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    RoutingLease lease = new(file, path, pending);
                    transferred = true;
                    return lease;
                }
                catch (IOException)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            throw new IOException("保存用途の排他を取得できません。");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            expired = error;
        }
        finally
        {
            if (!transferred)
            {
                using FileStream? abandoned = file;
                Release(path, pending, entered);
            }
        }
        throw new IOException("保存用途の排他を期限内に取得できません。", expired);
    }

    /// <summary>別プロセス向け排他を解放してから次のローカル待機者へ渡します。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        try
        {
            file.Dispose();
        }
        finally
        {
            Release(path, pending, true);
        }
    }

    /// <summary>取得失敗・取消でも参照数を戻し、使わなくなった保存先を保持しません。</summary>
    private static void Release(string path, Pending pending, bool entered)
    {
        using (gate.EnterScope())
        {
            if (entered)
            {
                pending.Queue.Release();
            }
            if (--pending.Users == 0)
            {
                waiting.Remove(path);
                using SemaphoreSlim completed = pending.Queue;
            }
        }
    }

    /// <summary>取得中・待機中の呼出しだけが共有する取得枠です。</summary>
    private sealed record Pending
    {
        /// <summary>非同期待機者へ順次取得枠を渡します。</summary>
        internal SemaphoreSlim Queue
        {
            get;
        } = new(1, 1);

        /// <summary>取得失敗も含めてReleaseで戻す所有数です。</summary>
        internal int Users
        {
            get; set;
        }
    }
}
