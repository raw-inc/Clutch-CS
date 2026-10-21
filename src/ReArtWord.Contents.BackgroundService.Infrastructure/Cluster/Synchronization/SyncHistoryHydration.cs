using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>通常参加と独立した所有Taskで、過去本文を少量ずつ取得し、バッチ間に通常処理へ時間を返します。</summary>
internal sealed class SyncHistoryHydration : IAsyncDisposable
{
    private readonly PeerCluster cluster;
    private readonly RecoveryRepository store;
    private readonly SyncDownload download;
    private readonly TimeProvider clock;
    private readonly SyncHistoryOffers offers;
    private readonly Lazy<Task> work;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> refresh = new(StringComparer.Ordinal);
    private HistorySynchronizationStatus status = new(0);

    /// <summary>既存の同期転送を再利用し、WFランタイムを起動する経路は持ちません。</summary>
    internal SyncHistoryHydration(PeerCluster cluster, RecoveryRepository store, SyncExportStore exports, SyncDownload download, TimeProvider clock, CancellationToken stopping)
    {
        this.cluster = cluster;
        this.store = store;
        this.download = download;
        this.clock = clock;
        offers = new(cluster, store, exports, clock, stopping);
        work = new(() => RunAsync(stopping));
    }

    /// <summary>API照会のたびにDBを読み直さない、小さい最新状態です。</summary>
    internal HistorySynchronizationStatus Status => Volatile.Read(ref status);

    /// <summary>取得元の履歴が整理・置換された場合だけ、現在の存在証明を取り直します。</summary>
    /// <returns>同じ相手の先行同期を再確認する必要があるかです。</returns>
    internal bool NeedsRefresh(string peerId) => refresh.ContainsKey(peerId);

    /// <summary>確認した先行同期より古い背景再同期要求を解除します。</summary>
    internal void Confirmed(string peerId) => refresh.TryRemove(peerId, out _);

    /// <summary>Core起動後に一つの背景Taskだけを開始します。</summary>
    // 抑制理由: workがTaskを所有し、DisposeAsyncで完了を必ず観測します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有するLazy Taskを停止時にawaitします。")]
    internal void Start() => _ = work.Value;

    /// <summary>同じ背景要求の準備状況を、現在値の取込みと区別して返します。</summary>
    /// <returns>固定した背景書出しの状態です。</returns>
    internal Task<SyncResponse> ReceiveAsync(SyncHistoryRequest request, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
        => offers.ReceiveAsync(request, peerId, instanceId, cancellationToken);

    /// <summary>書込みや生存通知の待機列を長い履歴取込みで占有しません。</summary>
    /// <returns>Core停止までの背景補完です。</returns>
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        TimeSpan delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
                await offers.CleanupAsync(cancellationToken).ConfigureAwait(false);
                HistorySynchronizationStatus current = await store.StatusAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref status, current);
                delay = TimeSpan.FromSeconds(1);
                if (current.Pending == 0 || !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
                {
                    continue;
                }
                long started = Stopwatch.GetTimestamp();
                await HydrateOneAsync(cancellationToken).ConfigureAwait(false);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                delay = TimeSpan.FromMilliseconds(Math.Clamp(elapsed * 4, 250, 5000));
            }
            catch (Exception error) when (SyncFailureException.IsExpected(error))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                HistorySynchronizationStatus previous = Status;
                Volatile.Write(ref status, previous with { Problem = SyncFailureException.CodeFor(error) });
                delay = TimeSpan.FromSeconds(5);
            }
        }
    }

    /// <summary>元の取得元を優先し、同じ検査値を持つ他の生存Peerも候補にします。</summary>
    /// <returns>一つの有限バッチの確認です。</returns>
    private async Task HydrateOneAsync(CancellationToken cancellationToken = default)
    {
        (string? preferred, IReadOnlyList<RecoveryRecord> receipts) = await store.PendingAsync(cancellationToken).ConfigureAwait(false);
        if (receipts.Count == 0)
        {
            return;
        }
        PeerInfo[] peers = cluster.GetPeers().Where(peer => peer.InstanceId != cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status))
            .OrderByDescending(peer => string.Equals(peer.PeerId, preferred, StringComparison.Ordinal)).Take(3).ToArray();
        if (peers.Length == 0)
        {
            Volatile.Write(ref status, Status with { Problem = "Sync.HistoryUnavailable" });
            return;
        }
        foreach (PeerInfo peer in peers)
        {
            long before = Status.Pending;
            try
            {
                await ReceiveBatchAsync(peer, receipts, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (SyncFailureException.IsExpected(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref status, Status with { Problem = SyncFailureException.CodeFor(error) });
                continue;
            }
            HistorySynchronizationStatus after = await store.StatusAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref status, after);
            string changed = "Sync.SnapshotChanged";
            if (string.Equals(after.Problem, changed, StringComparison.Ordinal) && string.Equals(peer.PeerId, preferred, StringComparison.Ordinal))
            {
                refresh.TryAdd(peer.PeerId, 0);
            }
            if (after.Pending < before)
            {
                return;
            }
        }
    }

    /// <summary>同じ要求IDだけを再照会し、完了・失敗のどちらでも元の書出しを解放します。</summary>
    /// <returns>存在証明のバッチに対応する確認です。</returns>
    private async Task ReceiveBatchAsync(PeerInfo peer, IReadOnlyList<RecoveryRecord> receipts, CancellationToken cancellationToken = default)
    {
        SyncHistoryRequest request = new(Guid.NewGuid(), receipts, clock.GetUtcNow().AddSeconds(30));
        using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(30), clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
        SyncManifest? manifest = null;
        try
        {
            SyncResponse response;
            do
            {
                WireMessage? reply = await cluster.ExchangeAsync("sync.history.open", peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(request), linked.Token).ConfigureAwait(false);
                response = reply?.Body.Deserialize<SyncResponse>() ?? throw new SyncFailureException("Sync.ResponseUnconfirmed");
                if (response.Pending)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), clock, linked.Token).ConfigureAwait(false);
                }
            }
            while (response.Pending);
            manifest = response.Manifest;
            if (manifest is null || !string.Equals(manifest.Phase, SyncManifest.HistoryPhase, StringComparison.Ordinal))
            {
                throw new SyncFailureException(response.Reason ?? "Sync.ManifestMismatch");
            }
            _ = await download.ImportAsync(manifest, peer.PeerId, peer.InstanceId, cancellationToken: linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (manifest is not null)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(1), clock);
                try
                {
                    _ = await cluster.ExchangeAsync("sync.close", peer.PeerId, peer.InstanceId,
                        JsonSerializer.SerializeToElement(new SyncCloseRequest(manifest.Id, Guid.Empty)), cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (SyncFailureException.IsExpected(error))
                {
                    // 応答を失っても、送信元が元の固定期限で回収します。
                }
            }
        }
    }

    /// <summary>通常通信の停止後に、所有した背景作業と元の書出しを回収します。</summary>
    /// <returns>所有Taskと転送資源の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (work.IsValueCreated)
        {
            await work.Value.ConfigureAwait(false);
        }
        await ((IAsyncDisposable)offers).DisposeAsync().ConfigureAwait(false);
    }
}
