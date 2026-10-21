namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Retention;

/// <summary>同じノードの並行入力が古い版から共有CASを繰り返すことを防ぎます。</summary>
internal sealed class NodeOutputRetentionQueue
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Workflow, string Node), Pending> pending = [];

    /// <summary>読取りから比較保存までの短い区間だけを実行単位を跨いで直列化します。</summary>
    /// <returns>保持値の本体処理を含めない保存区間の所有権です。</returns>
    internal async Task<IDisposable> EnterAsync(string workflow, string node, CancellationToken cancellationToken = default)
    {
        (string Workflow, string Node) key = (workflow, node);
        Pending current;
        using (gate.EnterScope())
        {
            if (!pending.TryGetValue(key, out current!))
            {
                current = new();
                pending.Add(key, current);
            }
            current.Users++;
        }
        bool entered = false;
        try
        {
            await current.Queue.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return new Lease(this, key, current);
        }
        finally
        {
            if (!entered)
            {
                Release(key, current, false);
            }
        }
    }

    /// <summary>待機取消も参照数へ反映し、利用が終わったノードを保持しません。</summary>
    private void Release((string Workflow, string Node) key, Pending current, bool entered)
    {
        using (gate.EnterScope())
        {
            if (entered)
            {
                current.Queue.Release();
            }
            if (--current.Users == 0)
            {
                pending.Remove(key);
                using SemaphoreSlim completed = current.Queue;
            }
        }
    }

    /// <summary>処理中・待機中の呼出しだけが共有する資源です。</summary>
    private sealed record Pending
    {
        internal SemaphoreSlim Queue
        {
            get;
        } = new(1, 1);

        internal int Users
        {
            get; set;
        }
    }

    /// <summary>同じ保存区間を一度だけ解放します。</summary>
    private sealed class Lease(NodeOutputRetentionQueue owner, (string Workflow, string Node) key, Pending pending) : IDisposable
    {
        private int disposed;

        /// <summary>保存完了または例外時に次の待機者へ渡します。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(key, pending, true);
            }
        }
    }
}
