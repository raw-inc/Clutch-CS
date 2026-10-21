using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>現在状態の双方向確認と過去本文の有限な背景取得を、既存の同期通信で所有します。</summary>
internal sealed class RepositorySynchronization : IAsyncDisposable
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly ClusterOptions options;
    private readonly TimeProvider clock;
    private readonly CancellationToken stopping;
    private readonly SyncExportStore exports;
    private readonly SyncDownload download;
    private readonly SyncRecovery recovery;
    private readonly SyncInventoryExchange inventory;
    private readonly SyncOutbound outgoing;
    private readonly SyncHistoryHydration? history;
    private readonly ConcurrentDictionary<(string Peer, Guid Instance, Guid Id), SyncIncoming> incoming = [];
    private readonly Lock admission = new();

    /// <summary>既存リポジトリと共通UDPだけを使用します。</summary>
    internal RepositorySynchronization(PeerCluster cluster, IDurableRepository repository, ClusterOptions options, TimeProvider clock,
        Func<Runtime.Networking.LocalNetworkBinding> binding, CancellationToken stopping)
    {
        this.cluster = cluster;
        this.repository = repository;
        this.options = options;
        this.clock = clock;
        this.stopping = stopping;
        recovery = new(clock);
        exports = new(cluster, repository, options, clock, binding);
        download = new(cluster, repository, exports, options, clock, binding);
        inventory = new(cluster, exports, options, clock, stopping);
        outgoing = new(cluster, repository, exports, download, recovery, options, clock, stopping);
        if (RecoveryRepository.Create(repository) is { } recoveryStore)
        {
            history = new(cluster, recoveryStore, exports, download, clock, stopping);
        }
    }

    /// <summary>通信資源を作る前に同期作業条件を検証します。</summary>
    internal static void ValidateOptions(ClusterOptions options)
    {
        if (options.SyncSessionLifetime < TimeSpan.FromSeconds(1) || TimeSpan.FromHours(1) < options.SyncSessionLifetime
            || options.MaximumSyncSnapshotBytes < 65536 || 1024L * 1024 * 1024 * 1024 < options.MaximumSyncSnapshotBytes
            || options.MaximumSyncSessions is < 1 or > 32)
        {
            throw new ArgumentException("同期作業の期限・容量・同時数が不正です。", nameof(options));
        }
    }

    /// <summary>最新の同期確認不能理由です。実値やローカルパスは含みません。</summary>
    internal string? Problem => recovery.Problem;

    /// <summary>通常参加を止めない背景履歴の取得状態です。</summary>
    internal HistorySynchronizationStatus? HistoryStatus => history?.Status;

    /// <summary>クラスタの通信開始後に背景履歴の復旧を開始します。</summary>
    internal void Start() => history?.Start();

    /// <summary>版一致後も残る同じ相手の転送未確認を再同期の対象にします。</summary>
    /// <returns>完全同期を再確認する必要があるかです。</returns>
    internal bool NeedsSynchronization(PeerInfo peer, bool conflictsResolved) => recovery.NeedsSynchronization(peer, conflictsResolved) || history?.NeedsRefresh(peer.PeerId) == true;

    /// <summary>失敗直後の同期を抑制し、ハートビートは継続します。</summary>
    /// <returns>次の同期を開始できるかです。</returns>
    internal bool CanSynchronize(PeerInfo peer) => recovery.CanSynchronize(peer);

    /// <summary>通常参加中の同じ実体と現在版だけを照合します。</summary>
    /// <returns>一致、変更中の再照合待ち、または完全確認が必要な結果です。</returns>
    internal Task<SyncVersionComparison> CompareVersionAsync(PeerInfo peer, CancellationToken cancellationToken = default) => outgoing.CompareVersionAsync(peer, cancellationToken);

    /// <summary>固定した同じ相手との双方向同期を確認します。</summary>
    /// <returns>全検証と取込みの確認結果です。</returns>
    internal async Task<bool> SynchronizeAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        bool completed = await outgoing.SynchronizeAsync(peer, cancellationToken).ConfigureAwait(false);
        if (completed)
        {
            history?.Confirmed(peer.PeerId);
        }
        return completed;
    }

    /// <summary>追従側の同期中も、準備済みの稼働側が処理を継続できる契約か確認します。</summary>
    /// <returns>双方が段階同期へ対応するかです。</returns>
    internal Task<bool> SupportsStagedAsync(PeerInfo peer, CancellationToken cancellationToken = default) => outgoing.SupportsStagedAsync(peer, cancellationToken);

    /// <summary>操作記録を作らない同期専用の有限操作を処理します。</summary>
    /// <returns>本文を含まない受付・確認、または小さい断片です。</returns>
    internal async Task<JsonElement> ReceiveAsync(WireMessage message, CancellationToken cancellationToken = default)
    {
        SyncResponse result;
        try
        {
            result = message.Kind switch
            {
                "sync.capabilities" => await CapabilitiesAsync(cancellationToken).ConfigureAwait(false),
                "sync.inventory" => await inventory.ReceiveAsync(message.Body.Deserialize<SyncInventoryRequest>() ?? throw new SyncFailureException("Sync.InventoryMismatch"), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false),
                "sync.delta.open" => await OpenDifferenceAsync(message.Body.Deserialize<SyncDeltaOpen>() ?? throw new SyncFailureException("Sync.ManifestMissing"), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false),
                "sync.version" => new(Version: await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false)),
                "sync.open" => await OpenAsync(message.Body.Deserialize<SyncManifest>() ?? throw new SyncFailureException("Sync.ManifestMissing"), message.SenderId, message.InstanceId, Guid.Empty, false, cancellationToken).ConfigureAwait(false),
                "sync.recovery.open" => await OpenRecoveryAsync(message.Body.Deserialize<SyncManifest>() ?? throw new SyncFailureException("Sync.ManifestMissing"), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false),
                "sync.history.open" => await (history ?? throw new SyncFailureException("Sync.ProtocolMissing")).ReceiveAsync(message.Body.Deserialize<SyncHistoryRequest>() ?? throw new SyncFailureException("Sync.ManifestMissing"), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false),
                "sync.result" => await ResultAsync(message.Body.Deserialize<Guid>(), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false),
                "sync.read" => new(Chunk: await exports.ReadAsync(message.Body.Deserialize<SyncReadRequest>() ?? throw new SyncFailureException("Sync.Range"), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false)),
                "sync.close" => await CloseAsync(message.Body.Deserialize<SyncCloseRequest>() ?? throw new SyncFailureException("Sync.Range"), message.SenderId, message.InstanceId, cancellationToken).ConfigureAwait(false),
                _ => new(Reason: "Sync.ProtocolMissing"),
            };
        }
        catch (Exception exception) when (SyncFailureException.IsExpected(exception))
        {
            result = new(Reason: SyncFailureException.CodeFor(exception));
        }
        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>固定指紋を収められる設定の場合だけ差分照合への対応を通知します。</summary>
    /// <returns>現在の通信設定で使用できる同期契約です。</returns>
    private async Task<SyncResponse> CapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        bool pending = repository is IPreparedOperations preparations && (await preparations.ReadPreparedIdsAsync(Guid.Empty, 1, cancellationToken).ConfigureAwait(false)).Count != 0;
        if (options.MaximumMessageBytes < SyncInventory.MinimumMessageBytes)
        {
            return new(HistoryRetention: true, StagedRecovery: RecoveryRepository.Create(repository) is not null, Pending: pending);
        }
        return new(Version: SyncInventoryExchange.Protocol, HistoryRetention: true, StagedRecovery: RecoveryRepository.Create(repository) is not null, Pending: pending);
    }

    /// <summary>対応する先行状態だけを受け付け、旧同期や背景本文と混同しません。</summary>
    /// <returns>原子的な先行取込みの状態です。</returns>
    private Task<SyncResponse> OpenRecoveryAsync(SyncManifest manifest, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (RecoveryRepository.Create(repository) is null || !string.Equals(manifest.Phase, SyncManifest.CurrentPhase, StringComparison.Ordinal))
        {
            throw new SyncFailureException("Sync.ProtocolMissing");
        }
        return OpenAsync(manifest, peerId, instanceId, Guid.Empty, true, cancellationToken);
    }

    /// <summary>同じ相手に保持した照合結果がある場合だけ差分の取込みを開始します。</summary>
    /// <returns>元差分に対する受入結果です。</returns>
    private Task<SyncResponse> OpenDifferenceAsync(SyncDeltaOpen request, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (request.Manifest.Phase is not null)
        {
            throw new SyncFailureException("Sync.ProtocolMissing");
        }
        if (request.ReverseId != Guid.Empty)
        {
            _ = exports.Manifest(request.ReverseId, peerId, instanceId);
        }
        return OpenAsync(request.Manifest, peerId, instanceId, request.ReverseId, true, cancellationToken);
    }

    /// <summary>同じマニフェストは一回の取込Taskだけを所有します。</summary>
    /// <returns>受付中または同じ元結果です。</returns>
    // 抑制理由: 受入Taskはincomingで保持し、結果照会とDisposeAsyncで必ず完了を観測します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "一回の同期をincomingが期限まで所有し、終了時に全Taskをawaitします。"),
     SuppressMessage("SourceQuality", "RAW141", Justification = "session所有権をincomingへ渡し、期限掃除と終了時に解放します。")]
    private async Task<SyncResponse> OpenAsync(SyncManifest manifest, string peerId, Guid instanceId, Guid reverseId, bool historyAware, CancellationToken cancellationToken = default)
    {
        download.Validate(manifest, peerId, instanceId);
        if (!historyAware && manifest.Phase is not null)
        {
            throw new SyncFailureException("Sync.ProtocolMissing");
        }
        if (!historyAware && (await repository.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(false)).HistoryBoundary is not null)
        {
            return new(Reason: "Sync.HistoryUpgradeRequired");
        }
        await CleanupAsync(cancellationToken).ConfigureAwait(false);
        SyncIncoming session;
        using (admission.EnterScope())
        {
            if (!incoming.TryGetValue((peerId, instanceId, manifest.Id), out SyncIncoming? known))
            {
                if (options.MaximumSyncSessions * 256 <= incoming.Count || options.MaximumSyncSessions <= incoming.Values.Count(static item => !item.Completed))
                {
                    return new(Reason: "Sync.ImportCapacity");
                }
                session = new(manifest, recovery.Revision, clock, token => ReceiveAndExportAsync(manifest, peerId, instanceId, reverseId, token), stopping, reverseId);
                incoming[(peerId, instanceId, manifest.Id)] = session;
            }
            else
            {
                session = known;
                if (session.Manifest != manifest || session.ReverseId != reverseId)
                {
                    return new(Reason: "Sync.ManifestMismatch");
                }
            }
            _ = session.Completion;
        }
        return await ReadResultAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一回の全検証・Importの後だけ逆方向書出しを作ります。</summary>
    /// <returns>相手が取得する固定マニフェストです。</returns>
    private async Task<SyncResponse> ReceiveAndExportAsync(SyncManifest manifest, string peerId, Guid instanceId, Guid reverseId, CancellationToken cancellationToken = default)
    {
        bool conflict = false;
        try
        {
            conflict = !await download.ImportAsync(manifest, peerId, instanceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (repository is IPreparedOperations preparations && (await preparations.ReadPreparedIdsAsync(Guid.Empty, 1, cancellationToken).ConfigureAwait(false)).Count != 0)
            {
                throw new SyncFailureException("Sync.PendingOperations");
            }
            if (reverseId != Guid.Empty)
            {
                return new(exports.Manifest(reverseId, peerId, instanceId), Conflict: conflict);
            }
            PeerInfo peer = cluster.GetPeers().First(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal) && item.InstanceId == instanceId);
            if (string.Equals(manifest.Phase, SyncManifest.CurrentPhase, StringComparison.Ordinal))
            {
                RecoveryRepository recoveryStore = RecoveryRepository.Create(repository) ?? throw new SyncFailureException("Sync.ProtocolMissing");
                Persistence.Documents.RepositorySnapshot current = await recoveryStore.ReadAsync(cluster.LocalPeerId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (0 < current.PendingOperations?.Count)
                {
                    throw new SyncFailureException("Sync.PendingOperations");
                }
                return new(await exports.CreatePayloadAsync(peer, current, SyncManifest.CurrentPhase, cancellationToken).ConfigureAwait(false), Conflict: conflict);
            }
            return new(await exports.CreateAsync(peer, false, cancellationToken).ConfigureAwait(false), Conflict: conflict);
        }
        catch (Exception exception) when (SyncFailureException.IsExpected(exception))
        {
            return new(Conflict: conflict, Reason: SyncFailureException.CodeFor(exception));
        }
    }

    /// <summary>再受入せず同じ元マニフェストの結果だけを照会します。</summary>
    /// <returns>受付中または確認済み結果です。</returns>
    private Task<SyncResponse> ResultAsync(Guid id, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (!incoming.TryGetValue((peerId, instanceId, id), out SyncIncoming? session))
        {
            return Task.FromResult(new SyncResponse(Reason: "Sync.SessionUnavailable"));
        }
        return ReadResultAsync(session, cancellationToken);
    }

    /// <summary>明示解放後の遅延パケットでImportを再実行しません。</summary>
    /// <returns>固定期限内の現在結果です。</returns>
    private async Task<SyncResponse> ReadResultAsync(SyncIncoming session, CancellationToken cancellationToken = default)
    {
        if (session.Closed || session.Manifest.ExpiresAt <= clock.GetUtcNow())
        {
            return new(Reason: "Sync.SessionUnavailable");
        }
        if (!session.Completed)
        {
            return new(Pending: true);
        }
        return await session.Completion.ConfigureAwait(false);
    }

    /// <summary>同じ元対象の作業だけを早期解放し、再配送拒否情報は期限まで保持します。</summary>
    /// <returns>解放の受付です。</returns>
    private async Task<SyncResponse> CloseAsync(SyncCloseRequest request, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        await inventory.CloseAsync(request.ImportId, peerId, instanceId, cancellationToken).ConfigureAwait(false);
        if (request.ExportId != Guid.Empty)
        {
            try
            {
                await exports.CloseAsync(request.ExportId, peerId, instanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (SyncFailureException)
            {
                // 書出しが先に期限切れでも、同じ送信実体の受入セッションは解放します。
            }
        }
        if (incoming.TryGetValue((peerId, instanceId, request.ImportId), out SyncIncoming? session))
        {
            bool confirm = request.Completed && !session.Closed && session.Completed && clock.GetUtcNow() < session.Manifest.ExpiresAt;
            session.Closed = true;
            session.Cancel();
            SyncResponse result = await session.Completion.ConfigureAwait(false);
            if (result.Manifest is not null)
            {
                await exports.ReleaseAsync(result.Manifest.Id, CancellationToken.None).ConfigureAwait(false);
                if (confirm && result.Reason is null && !result.Conflict && result.Manifest.Id == request.ExportId && clock.GetUtcNow() < result.Manifest.ExpiresAt)
                {
                    download.Validate(session.Manifest, peerId, instanceId);
                    recovery.Confirmed(peerId, instanceId, session.RecoveryRevision);
                    history?.Confirmed(peerId);
                }
            }
        }
        return new();
    }

    /// <summary>期限切れの作業ファイルと終了済み受付情報を回収します。</summary>
    /// <returns>期限切れ接続と所有ファイルの解放です。</returns>
    internal async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        inventory.Cleanup();
        await exports.CleanupAsync(cancellationToken).ConfigureAwait(false);
        using (admission.EnterScope())
        {
            foreach (KeyValuePair<(string Peer, Guid Instance, Guid Id), SyncIncoming> entry in incoming)
            {
                if (entry.Value.Manifest.ExpiresAt <= clock.GetUtcNow() && entry.Value.Completed && incoming.TryRemove(entry.Key, out SyncIncoming? removed))
                {
                    using SyncIncoming expired = removed;
                }
            }
        }
    }

    /// <summary>Host停止後に全受入作業を観測し、所有ファイルを解放します。</summary>
    /// <returns>同期作業の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        foreach (SyncIncoming session in incoming.Values)
        {
            session.Cancel();
        }
        await Task.WhenAll(incoming.Values.Select(static item => item.Completion)).ConfigureAwait(false);
        foreach (SyncIncoming session in incoming.Values)
        {
            session.Dispose();
        }
        incoming.Clear();
        if (history is not null)
        {
            await ((IAsyncDisposable)history).DisposeAsync().ConfigureAwait(false);
        }
        await ((IAsyncDisposable)inventory).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)exports).DisposeAsync().ConfigureAwait(false);
    }
}
