using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>差分照合の一回の書出しと、再配送に返す同じ結果を期限まで所有します。</summary>
internal sealed class SyncInventoryExchange(PeerCluster cluster, SyncExportStore exports, ClusterOptions options, TimeProvider clock, CancellationToken stopping) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string Peer, Guid Instance, Guid Id), Offer> offers = [];
    private readonly Lock admission = new();

    /// <summary>旧版の同期契約へ差分だけを誤って送らないための対応識別子です。</summary>
    internal static string Protocol => "inventory-v1";

    /// <summary>照合ごとの固定要求から一つの差分だけを準備します。</summary>
    /// <returns>同じ元要求について準備した差分です。</returns>
    // 抑制理由: Offerの所有権をoffersへ移し、期限掃除と終了時に解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "Offerを元要求の期限までoffersで所有します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Offerの所有権をoffersへ移します。") ]
    internal async Task<SyncResponse> ReceiveAsync(SyncInventoryRequest request, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        SyncInventory.Validate(request.Hashes);
        if (request.Id == Guid.Empty || request.ExpiresAt <= clock.GetUtcNow() || clock.GetUtcNow().AddHours(1) < request.ExpiresAt)
        {
            throw new SyncFailureException("Sync.SessionExpired");
        }
        PeerInfo peer = cluster.GetPeers().FirstOrDefault(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal)
            && item.InstanceId == instanceId && item.Status != EPeerStatus.Detached && clock.GetUtcNow() - item.LastSeen <= options.PeerTimeout)
            ?? throw new SyncFailureException("Sync.SourceInstanceUnavailable");
        Cleanup();
        Offer offer;
        string fingerprint = ContractJson.Hash(request);
        using (admission.EnterScope())
        {
            if (!offers.TryGetValue((peerId, instanceId, request.Id), out Offer? known))
            {
                if (options.MaximumSyncSessions * 256 <= offers.Count)
                {
                    throw new SyncFailureException("Sync.ExportCapacity");
                }
                offer = new(fingerprint, request.ExpiresAt, token => CreateAsync(peer, request, token));
                offers[(peerId, instanceId, request.Id)] = offer;
            }
            else
            {
                offer = known;
                if (!string.Equals(fingerprint, offer.Fingerprint, StringComparison.Ordinal))
                {
                    throw new SyncFailureException("Sync.InventoryMismatch");
                }
            }
            if (offer.Closed)
            {
                return new(Reason: "Sync.SessionUnavailable");
            }
        }
        if (request.AllowPending && !offer.Result.Value.IsCompleted)
        {
            return new(Pending: true);
        }
        SyncResponse response;
        try
        {
            response = await offer.Result.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (offer.ClosingToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(Reason: "Sync.SessionUnavailable");
        }
        using (admission.EnterScope())
        {
            if (offer.Closed)
            {
                return new(Reason: "Sync.SessionUnavailable");
            }
        }
        return response;
    }

    /// <summary>照合応答を失った送信元も、元要求IDだけで所有書出しを終了できます。</summary>
    /// <returns>作成中の一回を観測し、完了時にその作業枠を解放する確認です。</returns>
    // 抑制理由: Offerは辞書が所有し、取消Taskはロック外で必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "辞書所有のOfferを借用します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "cancelをロック外で必ずawaitします。") ]
    internal async Task CloseAsync(Guid id, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Offer? offer;
        Task cancel;
        using (admission.EnterScope())
        {
            if (!offers.TryGetValue((peerId, instanceId, id), out offer))
            {
                return;
            }
            offer.Closed = true;
            cancel = offer.CancelAsync(CancellationToken.None);
        }
        try
        {
            // 作成そのものを取り消してから終了を観測し、呼出側の期限で所有作業を取り残しません。
            await cancel.ConfigureAwait(false);
            SyncResponse response = await offer.Result.Value.ConfigureAwait(false);
            if (response.Inventory is { } inventory)
            {
                await exports.ReleaseAsync(inventory.Manifest.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (SyncFailureException.IsExpected(error))
        {
            // 作成失敗時はCreateAsyncが解放します。元要求は閉じたまま保持し、再配送で作り直しません。
        }
    }

    /// <summary>固定期限を過ぎた完了記録だけを回収します。</summary>
    internal void Cleanup()
    {
        using (admission.EnterScope())
        {
            foreach (KeyValuePair<(string Peer, Guid Instance, Guid Id), Offer> pair in offers)
            {
                if (pair.Value.ExpiresAt <= clock.GetUtcNow() && pair.Value.Result.IsValueCreated && pair.Value.Result.Value.IsCompleted
                    && pair.Value.CancellationCompleted && offers.TryRemove(pair.Key, out Offer? removed))
                {
                    using Offer expired = removed;
                }
            }
        }
    }

    /// <summary>応答を返す前に失敗した場合は、依頼側がまだ知らない書出しを解放します。</summary>
    /// <returns>差分の準備結果です。</returns>
    private async Task<SyncResponse> CreateAsync(PeerInfo peer, SyncInventoryRequest request, CancellationToken cancellationToken = default)
    {
        TimeSpan remaining = request.ExpiresAt - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            throw new SyncFailureException("Sync.SessionExpired");
        }
        using CancellationTokenSource expiry = new(remaining, clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken, stopping);
        SyncManifest? manifest = null;
        bool completed = false;
        try
        {
            SyncInventoryOffer offer = await exports.CreateOfferAsync(peer, request, linked.Token).ConfigureAwait(false);
            manifest = offer.Manifest;
            completed = true;
            return new(Inventory: offer);
        }
        finally
        {
            if (!completed && manifest is not null)
            {
                await exports.ReleaseAsync(manifest.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>通信停止後も所有した照合作業を観測してから参照を解放します。</summary>
    /// <returns>全照合作業の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            Task[] cancellations;
            using (admission.EnterScope())
            {
                cancellations = offers.Values.Select(offer => offer.CancelAsync()).ToArray();
            }
            await Task.WhenAll(cancellations).ConfigureAwait(false);
            await Task.WhenAll(offers.Values.Where(offer => offer.Result.IsValueCreated).Select(offer => offer.Result.Value)).ConfigureAwait(false);
        }
        catch (Exception error) when (SyncFailureException.IsExpected(error))
        {
            // 終了時にも、所有した失敗Taskを未観測で残しません。
        }
        foreach (Offer offer in offers.Values)
        {
            using Offer completed = offer;
        }
        offers.Clear();
    }

    /// <summary>同じ元要求へ返す、一回の固定結果です。</summary>
    private sealed class Offer : IDisposable
    {
        private readonly CancellationTokenSource closing = new();
        private Task? cancellation;

        /// <summary>一回の作成と終了用取消を、同じ元要求へ結び付けます。</summary>
        internal Offer(string fingerprint, DateTimeOffset expiresAt, Func<CancellationToken, Task<SyncResponse>> create)
        {
            Fingerprint = fingerprint;
            ExpiresAt = expiresAt;
            ClosingToken = closing.Token;
            Result = new(() => Task.Run(() => create(ClosingToken), CancellationToken.None));
        }

        internal string Fingerprint
        {
            get;
        }

        internal DateTimeOffset ExpiresAt
        {
            get;
        }

        internal Lazy<Task<SyncResponse>> Result
        {
            get;
        }

        internal CancellationToken ClosingToken
        {
            get;
        }

        internal bool CancellationCompleted => cancellation is null || cancellation.IsCompleted;

        /// <summary>admission内で一度だけ取消を開始し、その完了を所有します。</summary>
        /// <returns>取消コールバックの完了です。</returns>
        internal Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cancellation ??= closing.CancelAsync();
        }

        /// <summary>送信元が中断した元要求です。読取りと変更はadmissionで直列化します。</summary>
        internal bool Closed
        {
            get; set;
        }

        /// <summary>作成と取消の完了後だけ、元要求の取消資源を解放します。</summary>
        public void Dispose() => closing.Dispose();
    }
}
