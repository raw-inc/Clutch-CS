using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>照合中は固定メタデータと一枠だけを所有し、本文ファイルの作成は相手の指紋を受け取ってから行います。</summary>
internal sealed class SyncPreparedExport(SyncExportStore owner, PeerInfo target, FrozenSnapshotExport frozen, SyncInventoryRequest request, SemaphoreSlim reservation) : IDisposable
{
    private readonly Lock gate = new();
    private FrozenSnapshotExport? source = frozen;
    private int state;

    /// <summary>準備を行った相手実体です。</summary>
    internal PeerInfo Target => target;

    /// <summary>呼出元による配列変更から独立した照合要求です。</summary>
    internal SyncInventoryRequest Request => request with { Hashes = request.Hashes.ToArray(), Boundary = request.Boundary is null ? null
                                                                                                                                   : ContractJson.Copy(request.Boundary) };

    /// <summary>同じ準備の期限を延長せず本文を作成するための元情報です。</summary>
    internal SyncInventoryRequest Original => request;

    /// <summary>全履歴本文や読取り取引を保持しない固定参照です。</summary>
    internal FrozenSnapshotExport Source => source ?? throw new ObjectDisposedException(nameof(SyncPreparedExport));

    /// <summary>対応媒体だけで一枠を予約し、作成失敗・取消では直ちに返却します。</summary>
    /// <returns>未対応媒体なら従来の完全書出しへ戻すnullです。</returns>
    // 抑制理由: 成功時の予約所有者を呼出元へ返し、同期処理のusing範囲へ所有権を移します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "作成した準備の所有権を呼出元へ返します。作成失敗時はfinallyで予約を解放します。")]
    internal static async Task<SyncPreparedExport?> CreateAsync(SyncExportStore owner, PeerInfo target, IDurableRepository repository, SemaphoreSlim reservation,
        SemaphoreSlim materialization, ClusterOptions options, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        if (repository is not ISnapshotExportSource { SupportsDeferredReceiptReads: true })
        {
            return null;
        }
        if (!await reservation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SyncFailureException("Sync.ExportCapacity");
        }
        bool retained = false, entered = false;
        try
        {
            await materialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            FrozenSnapshotExport frozen = await SnapshotExport.FreezeAsync(repository, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Deferred snapshot support changed.");
            SyncInventoryRequest request = new(Guid.NewGuid(), SyncInventory.Describe(frozen.Description), clock.GetUtcNow() + options.SyncSessionLifetime,
                SyncInventory.Metadata(frozen.Description.Snapshot).HistoryBoundary);
            SyncPreparedExport result = new(owner, target, frozen, request, reservation);
            retained = true;
            return result;
        }
        finally
        {
            if (entered)
            {
                materialization.Release();
            }
            if (!retained)
            {
                reservation.Release();
            }
        }
    }

    /// <summary>別Store・期限切れ・再利用を拒否して、一回の本文作成だけを許可します。</summary>
    internal void Begin(SyncExportStore expectedOwner, DateTimeOffset now)
    {
        using (gate.EnterScope())
        {
            if (!ReferenceEquals(owner, expectedOwner) || state != 0)
            {
                throw new SyncFailureException("Sync.SessionUnavailable");
            }
            if (request.ExpiresAt <= now)
            {
                throw new SyncFailureException("Sync.SessionExpired");
            }
            state = 1;
        }
    }

    /// <summary>完全な固定ファイルを公開した場合だけ、その所有者へ予約枠を引き渡します。</summary>
    /// <returns>一回だけ公開した転送マニフェストです。</returns>
    internal SyncManifest Publish(Func<SyncManifest> publish)
    {
        using (gate.EnterScope())
        {
            if (state != 1)
            {
                throw new SyncFailureException("Sync.SessionUnavailable");
            }
            SyncManifest manifest = publish();
            state = 2;
            source = null;
            return manifest;
        }
    }

    /// <summary>未公開の予約枠を一度だけ戻し、固定値も保持し続けません。</summary>
    public void Dispose()
    {
        using (gate.EnterScope())
        {
            if (state < 2)
            {
                reservation.Release();
            }
            state = 3;
            source = null;
        }
    }
}
