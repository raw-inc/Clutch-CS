using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>依頼側の固定書出し、差分照合、双方向の完了確認を一回の寿命で所有します。</summary>
internal sealed class SyncOutbound(PeerCluster cluster, IDurableRepository repository, SyncExportStore exports, SyncDownload download,
    SyncRecovery recovery, ClusterOptions options, TimeProvider clock, CancellationToken stopping)
{
    /// <summary>両端の読取り中や再照合の間に版が変わった場合は、安定した不一致と区別します。</summary>
    /// <returns>一致版、有限の再照合待ち、または実同期が必要な結果です。</returns>
    internal async Task<SyncVersionComparison> CompareVersionAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        try
        {
            string? previousLocal = null, previousRemote = null;
            bool changed = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string before = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
                SyncResponse response = await ExchangeAsync("sync.version", peer, Guid.Empty, cancellationToken).ConfigureAwait(false);
                if (response.Reason is not null || string.IsNullOrWhiteSpace(response.Version))
                {
                    return new();
                }
                string current = before;
                if (!string.Equals(before, response.Version, StringComparison.Ordinal))
                {
                    current = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
                }
                if (string.Equals(current, response.Version, StringComparison.Ordinal))
                {
                    // 照会中に次のコミットへ進んでも、今回両端が確認した一致点を捨てません。
                    recovery.DeferChangingVersion(peer, false);
                    return new(response.Version);
                }
                changed |= !string.Equals(before, current, StringComparison.Ordinal)
                    || previousLocal is not null && !string.Equals(previousLocal, current, StringComparison.Ordinal)
                    || previousRemote is not null && !string.Equals(previousRemote, response.Version, StringComparison.Ordinal);
                previousLocal = current;
                previousRemote = response.Version;
                if (attempt == 0)
                {
                    await Task.Delay(options.HeartbeatInterval, clock, cancellationToken).ConfigureAwait(false);
                }
            }
            return new(RetryLater: recovery.DeferChangingVersion(peer, changed));
        }
        catch (Exception exception) when (SyncFailureException.IsExpected(exception))
        {
            return new();
        }
    }

    /// <summary>ローカル固定書出しを相手へ取り込ませ、相手の固定書出しも完全確認します。</summary>
    /// <returns>双方向のImportと競合確認が完了したかです。</returns>
    internal async Task<bool> SynchronizeAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        if (repository is IPreparedOperations preparations && (await preparations.ReadPreparedIdsAsync(Guid.Empty, 1, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            // 準備中は先行同期を確定できません。過去本文を列挙する前に元操作の回復へ譲ります。
            return false;
        }
        SyncManifest? offered = null;
        SyncManifest? received = null;
        Guid negotiation = Guid.Empty;
        long startedRevision = recovery.Revision;
        bool completed = false;
        try
        {
            SyncResponse capabilities = await ProtocolAsync(peer, cancellationToken).ConfigureAwait(false);
            if (capabilities.Pending)
            {
                recovery.Failed(peer, "Sync.PendingOperations");
                return false;
            }
            RecoveryRepository? recoveryStore = RecoveryRepository.Create(repository);
            bool staged = capabilities.StagedRecovery && recoveryStore is not null;
            bool inventory = !staged && SyncInventory.MinimumMessageBytes <= options.MaximumMessageBytes
                && string.Equals(capabilities.Version, SyncInventoryExchange.Protocol, StringComparison.Ordinal);
            using SyncPreparedExport? prepared = inventory ? await exports.PrepareAsync(peer, cancellationToken).ConfigureAwait(false)
                                                           : null;
            if (staged)
            {
                Persistence.Documents.RepositorySnapshot current = await recoveryStore!.ReadAsync(cluster.LocalPeerId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                if (0 < current.PendingOperations?.Count)
                {
                    throw new SyncFailureException("Sync.PendingOperations");
                }
                offered = await exports.CreatePayloadAsync(peer, current, SyncManifest.CurrentPhase, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (prepared is null)
                {
                    offered = await exports.CreateAsync(peer, inventory, cancellationToken).ConfigureAwait(false);
                }
            }
            negotiation = prepared?.Original.Id ?? offered!.Id;
            DateTimeOffset expires = prepared?.Original.ExpiresAt ?? offered!.ExpiresAt;
            using CancellationTokenSource expiry = new(expires - clock.GetUtcNow(), clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
            bool historyBoundary = prepared is null ? exports.HasHistoryBoundary(offered!)
                                                    : prepared.Original.Boundary is not null;
            if (historyBoundary && !capabilities.HistoryRetention)
            {
                throw new SyncFailureException("Sync.HistoryUpgradeRequired");
            }
            if (inventory)
            {
                SyncInventoryRequest inventoryRequest = (prepared?.Request ?? exports.Describe(offered!)) with { AllowPending = true };
                SyncResponse compared = await ExchangeAsync("sync.inventory", peer, inventoryRequest, linked.Token).ConfigureAwait(false);
                while (compared.Pending)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), clock, linked.Token).ConfigureAwait(false);
                    compared = await ExchangeAsync("sync.inventory", peer, inventoryRequest, linked.Token).ConfigureAwait(false);
                }
                SyncInventoryOffer proposed = compared.Inventory ?? throw new SyncFailureException(compared.Reason ?? "Sync.InventoryMismatch");
                received = proposed.Manifest;
                download.Validate(received, peer.PeerId, peer.InstanceId);
                if (prepared is not null)
                {
                    offered = await exports.MaterializeAsync(prepared, proposed.Full ? null
                                                                                     : proposed.Hashes, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    if (!proposed.Full)
                    {
                        offered = (await exports.NarrowAsync(offered!, proposed.Hashes, linked.Token).ConfigureAwait(false)).Manifest;
                    }
                }
            }
            SyncManifest outgoing = offered ?? throw new SyncFailureException("Sync.ManifestMissing");
            SyncResponse result;
            if (staged)
            {
                result = await ExchangeAsync("sync.recovery.open", peer, outgoing, linked.Token).ConfigureAwait(false);
            }
            else
            {
                if (received is null && !capabilities.HistoryRetention)
                {
                    result = await ExchangeAsync("sync.open", peer, outgoing, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    result = await ExchangeAsync("sync.delta.open", peer, new SyncDeltaOpen(outgoing, received?.Id ?? Guid.Empty), linked.Token).ConfigureAwait(false);
                }
            }
            while (result.Pending)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), clock, linked.Token).ConfigureAwait(false);
                result = await ExchangeAsync("sync.result", peer, outgoing.Id, linked.Token).ConfigureAwait(false);
            }
            if (result.Manifest is null || (result.Reason is not null && !result.Conflict))
            {
                recovery.Failed(peer, result.Reason ?? "Sync.RemoteImportUnconfirmed");
                return false;
            }
            if (received is not null && received != result.Manifest)
            {
                throw new SyncFailureException("Sync.ManifestMismatch");
            }
            received = result.Manifest;
            if (staged != string.Equals(received.Phase, SyncManifest.CurrentPhase, StringComparison.Ordinal))
            {
                throw new SyncFailureException("Sync.ManifestMismatch");
            }
            bool merged = await download.ImportAsync(received, peer.PeerId, peer.InstanceId, result.Conflict, linked.Token).ConfigureAwait(false);
            if (!merged || result.Conflict)
            {
                recovery.Failed(peer, "Sync.Conflict");
                await cluster.RefreshParticipationAsync(linked.Token).ConfigureAwait(false);
                return cluster.GetPeers().Any(item => item.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(item.Status));
            }
            download.Validate(received, peer.PeerId, peer.InstanceId);
            recovery.Confirmed(peer.PeerId, peer.InstanceId, startedRevision);
            completed = true;
            return true;
        }
        catch (Exception exception) when (SyncFailureException.IsExpected(exception))
        {
            if (!stopping.IsCancellationRequested)
            {
                recovery.Failed(peer, SyncFailureException.CodeFor(exception));
            }
            return false;
        }
        finally
        {
            if (offered is not null)
            {
                await exports.ReleaseAsync(offered.Id, CancellationToken.None).ConfigureAwait(false);
            }
            if (negotiation != Guid.Empty)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(1), clock);
                try
                {
                    await ExchangeAsync("sync.close", peer, new SyncCloseRequest(received?.Id ?? Guid.Empty, negotiation, completed), cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (SyncFailureException.IsExpected(exception))
                {
                    // 通信できない相手も、同じ固定期限で作業資材を解放します。
                }
            }
        }
    }

    /// <summary>対応相手の追従待ちで、現在稼働できるPeer自身をSyncingへ戻しません。</summary>
    /// <returns>先行同期の保存境界を双方が理解するかです。</returns>
    internal async Task<bool> SupportsStagedAsync(PeerInfo peer, CancellationToken cancellationToken = default)
        => RecoveryRepository.Create(repository) is not null && (await ProtocolAsync(peer, cancellationToken).ConfigureAwait(false)).StagedRecovery;

    /// <summary>副作用のない対応照会で、旧版へ差分だけを渡すことを防ぎます。</summary>
    /// <returns>相手が同じ区分照合を扱える場合だけtrueです。</returns>
    private async Task<SyncResponse> ProtocolAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource probe = new(TimeSpan.FromSeconds(2), clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(probe.Token, cancellationToken);
        try
        {
            SyncResponse response = await ExchangeAsync("sync.capabilities", peer, Guid.Empty, linked.Token).ConfigureAwait(false);
            return response;
        }
        catch (Exception error) when (SyncFailureException.IsExpected(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new();
        }
    }

    /// <summary>共通UDPの宛先・元の実体・再配送をそのまま使用します。</summary>
    /// <returns>小さい同期応答です。</returns>
    private async Task<SyncResponse> ExchangeAsync<T>(string kind, PeerInfo peer, T body, CancellationToken cancellationToken = default)
    {
        WireMessage? reply = await cluster.ExchangeAsync(kind, peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(body), cancellationToken).ConfigureAwait(false);
        SyncResponse response = reply?.Body.Deserialize<SyncResponse>() ?? throw new SyncFailureException("Sync.ResponseUnconfirmed");
        if (response.Reason is not null)
        {
            return response with { Reason = SyncFailureException.Normalize(response.Reason) };
        }
        return response;
    }

}
