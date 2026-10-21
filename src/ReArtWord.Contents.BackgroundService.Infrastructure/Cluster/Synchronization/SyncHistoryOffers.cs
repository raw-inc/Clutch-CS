using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>背景履歴の同じ要求を一回の書出しへ結び付け、固定期限で作業資源を解放します。</summary>
internal sealed class SyncHistoryOffers(PeerCluster cluster, RecoveryRepository store, SyncExportStore exports, TimeProvider clock, CancellationToken stopping) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Peer, Guid Instance, Guid Request), Offer> offers = [];

    /// <summary>元の相手・内容・期限が一致する要求へ同じ書出し状態を返します。</summary>
    /// <returns>準備中または元マニフェストです。</returns>
    // 抑制理由: Offerが全Taskと取消資源を所有し、CleanupAsyncとDisposeAsyncで完了を観測して解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "Offerの所有権をoffersへ渡し、終了経路で回収します。")]
    internal Task<SyncResponse> ReceiveAsync(SyncHistoryRequest request, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Id == Guid.Empty || request.Receipts is null || request.Receipts.Count is < 1 or > 32
            || request.ExpiresAt <= clock.GetUtcNow() || clock.GetUtcNow().AddMinutes(1) < request.ExpiresAt)
        {
            throw new SyncFailureException("Sync.Range");
        }
        foreach (RecoveryRecord receipt in request.Receipts)
        {
            RecoveryRecordLedger.Validate(receipt);
        }
        PeerInfo target = cluster.GetPeers().SingleOrDefault(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal) && peer.InstanceId == instanceId)
            ?? throw new SyncFailureException("Sync.SourceInstanceUnavailable");
        string fingerprint = ContractJson.Hash(request);
        using (gate.EnterScope())
        {
            (string, Guid, Guid) key = (peerId, instanceId, request.Id);
            if (!offers.TryGetValue(key, out Offer? offer))
            {
                if (128 <= offers.Count || 4 <= offers.Values.Count(item => !item.Work.IsCompleted))
                {
                    return Task.FromResult(new SyncResponse(Reason: "Sync.ExportCapacity"));
                }
                offer = new(fingerprint, request.ExpiresAt, clock, token => CreateAsync(target, request, token), stopping);
                offers.Add(key, offer);
            }
            if (!string.Equals(offer.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new SyncFailureException("Sync.ManifestMismatch");
            }
            return offer.Work.IsCompleted ? offer.Work
                                          : Task.FromResult(new SyncResponse(Pending: true));
        }
    }

    /// <summary>作成開始を受信スレッドから分離し、背景本文を現在値の転送と区別します。</summary>
    /// <returns>元の有限バッチに対応する書出しです。</returns>
    private async Task<SyncResponse> CreateAsync(PeerInfo target, SyncHistoryRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        try
        {
            IReadOnlyList<RecoveryRecordBody> bodies = await store.ReadBodiesAsync(request.Receipts, cancellationToken).ConfigureAwait(false);
            RepositorySnapshot snapshot = new([], [], RecoveredHistory: bodies);
            return new(await exports.CreatePayloadAsync(target, snapshot, SyncManifest.HistoryPhase, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (SyncFailureException.IsExpected(error))
        {
            return new(Reason: SyncFailureException.CodeFor(error));
        }
    }

    /// <summary>期限を過ぎた元要求を先に取り除き、取消後の作業完了を待って解放します。</summary>
    /// <returns>期限切れ資材の回収です。</returns>
    internal async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Offer[] expired;
        using (gate.EnterScope())
        {
            KeyValuePair<(string Peer, Guid Instance, Guid Request), Offer>[] selected = offers.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow()).ToArray();
            expired = selected.Select(pair => pair.Value).ToArray();
            foreach (KeyValuePair<(string Peer, Guid Instance, Guid Request), Offer> pair in selected)
            {
                offers.Remove(pair.Key);
            }
        }
        foreach (Offer offer in expired)
        {
            await ReleaseAsync(offer, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>受信元がcloseを失っても、元の固定書出しを一回だけ解放します。</summary>
    /// <returns>通信と取消資源の終了です。</returns>
    private async Task ReleaseAsync(Offer offer, CancellationToken cancellationToken = default)
    {
        using (offer)
        {
            offer.Cancel();
            SyncResponse result = await offer.Work.ConfigureAwait(false);
            if (result.Manifest is { } manifest)
            {
                await exports.ReleaseAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>全所有作業を観測してから通信資源を返します。</summary>
    /// <returns>すべての元要求の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Offer[] remaining;
        using (gate.EnterScope())
        {
            remaining = offers.Values.ToArray();
            offers.Clear();
        }
        foreach (Offer offer in remaining)
        {
            await ReleaseAsync(offer).ConfigureAwait(false);
        }
    }

    /// <summary>再照会で延長しない期限と一つの作成Taskを所有します。</summary>
    private sealed class Offer : IDisposable
    {
        private readonly CancellationTokenSource expiry;
        private readonly CancellationTokenSource lifetime;
        private readonly Lazy<Task<SyncResponse>> work;

        /// <summary>受入時の指紋・期限を固定して作成を開始します。</summary>
        internal Offer(string fingerprint, DateTimeOffset expires, TimeProvider clock, Func<CancellationToken, Task<SyncResponse>> create, CancellationToken stopping)
        {
            Fingerprint = fingerprint;
            ExpiresAt = expires;
            expiry = new(expires - clock.GetUtcNow(), clock);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, stopping);
            work = new(() => create(lifetime.Token));
        }

        /// <summary>再送との照合に使う元の要求指紋です。</summary>
        internal string Fingerprint
        {
            get;
        }

        /// <summary>再送で延長しない固定期限です。</summary>
        internal DateTimeOffset ExpiresAt
        {
            get;
        }

        /// <summary>所有者が終了まで観測する一回の作成です。</summary>
        internal Task<SyncResponse> Work => work.Value;

        /// <summary>元の一回へだけ中止を通知します。</summary>
        internal void Cancel() => lifetime.Cancel();

        /// <summary>作業終了後の取消監視を解放します。</summary>
        public void Dispose()
        {
            lifetime.Dispose();
            expiry.Dispose();
        }
    }
}
