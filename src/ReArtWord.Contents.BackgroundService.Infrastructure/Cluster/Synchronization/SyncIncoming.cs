namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>同じマニフェストの再配送に同じ一回の取込結果を返します。</summary>
internal sealed class SyncIncoming : IDisposable
{
    private readonly CancellationTokenSource expiry;
    private readonly CancellationTokenSource lifetime;
    private readonly Lazy<Task<SyncResponse>> work;

    /// <summary>最初の固定期限を使い、再送で延長しません。</summary>
    internal SyncIncoming(SyncManifest manifest, long recoveryRevision, TimeProvider clock, Func<CancellationToken, Task<SyncResponse>> action, CancellationToken stopping, Guid reverseId = default)
    {
        Manifest = manifest;
        RecoveryRevision = recoveryRevision;
        ReverseId = reverseId;
        expiry = new(manifest.ExpiresAt - clock.GetUtcNow(), clock);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, stopping);
        work = new(() => action(lifetime.Token));
    }

    /// <summary>同一内容を照合する元マニフェストです。</summary>
    internal SyncManifest Manifest
    {
        get;
    }

    /// <summary>この受入より後に生じた失敗を完了通知で解除しないための境界です。</summary>
    internal long RecoveryRevision
    {
        get;
    }

    /// <summary>差分照合で固定した逆方向書出しです。空値は従来の全体同期です。</summary>
    internal Guid ReverseId
    {
        get;
    }

    /// <summary>一回だけ開始する取込です。</summary>
    internal Task<SyncResponse> Completion => work.Value;

    /// <summary>終了確認済みなら重い作業を追加しません。</summary>
    internal bool Completed => work.IsValueCreated && work.Value.IsCompleted;

    /// <summary>明示解放後も固定期限までは再Importを拒否します。</summary>
    internal bool Closed
    {
        get; set;
    }

    /// <summary>未完了の受入を中止し、完全検証前のImportを防ぎます。</summary>
    internal void Cancel() => lifetime.Cancel();

    /// <summary>終了済みセッションの期限監視資源を解放します。</summary>
    public void Dispose()
    {
        lifetime.Dispose();
        expiry.Dispose();
    }
}
