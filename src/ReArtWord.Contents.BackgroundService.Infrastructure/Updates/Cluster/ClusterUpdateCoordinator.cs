using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;

/// <summary>固定対象を一リリースへ更新し事前に指定した補償方針を適用します。</summary>
public sealed class ClusterUpdateCoordinator
{
    private readonly IDurableRepository repository;
    private readonly IUpdatePeerTransport transport;
    private readonly string? localPeerId;

    /// <summary>共通保存と具体Peer配送を使用し、実行元Peerは最後に更新します。</summary>
    public ClusterUpdateCoordinator(IDurableRepository repository, IUpdatePeerTransport transport, string? localPeerId = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(transport);
        this.repository = repository;
        this.transport = transport;
        this.localPeerId = localPeerId;
    }

    /// <summary>一回の保存済み状態だけを読み、副作用を再送しません。</summary>
    /// <returns>固定入力と各Peerの確認済み結果です。</returns>
    public async ValueTask<ClusterUpdateStatus?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("updates", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<ClusterUpdateStatus>(ContractJson.Options);
    }

    /// <summary>準備を全対象で確認してから一件ずつ更新し、既知成功だけを補償します。</summary>
    /// <returns>受理ではなく全対象の最終確認結果です。</returns>
    public async Task<ClusterUpdateStatus> ExecuteAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReleaseSelector.Validate(request);
        ArgumentNullException.ThrowIfNull(request.PeerIds);
        if (request.OperationId == Guid.Empty || request.PeerIds.Count == 0 || request.PeerIds.Any(string.IsNullOrWhiteSpace) || request.PeerIds.Distinct(StringComparer.Ordinal).Count() != request.PeerIds.Count || !Enum.IsDefined(request.FailurePolicy) || !Enum.IsDefined(request.RollbackFailurePolicy) || request.PeerTimeoutMilliseconds < 1)
        {
            throw new ArgumentException("固定Peer集合、更新識別子と事前方針が必要です。", nameof(request));
        }
        ClusterUpdateRequest owned = ContractJson.Copy(request);
        ClusterUpdateStatus? old = await ReadAsync(owned.OperationId, cancellationToken).ConfigureAwait(false);
        if (old is not null)
        {
            if (!string.Equals(ContractJson.Hash(old.Request), ContractJson.Hash(owned), StringComparison.Ordinal))
            {
                return new ClusterUpdateStatus(owned, EOutcome.NotExecuted, true, [], "update.operationMismatch");
            }
            return old;
        }
        List<UpdatePeerResult> peers = [];
        ClusterUpdateStatus status = new(owned, EOutcome.Unknown, false, peers.ToArray());
        await SaveAsync(status, true, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ClusterUpdateRequest> selections = new(StringComparer.Ordinal);
        try
        {
            foreach (string peer in owned.PeerIds)
            {
                string platform = await transport.GetPlatformAsync(peer, cancellationToken).ConfigureAwait(false);
                ClusterUpdateRequest selected = ReleaseSelector.Select(owned, platform);
                await ReleaseVerifier.VerifyAsync(selected.Manifest, selected.PayloadRoot, cancellationToken).ConfigureAwait(false);
                selections.Add(peer, selected);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            status = status with { Outcome = EOutcome.NotExecuted, Completed = true, Reason = "update.platformPreflightFailed" };
            await SaveAsync(status, false, CancellationToken.None).ConfigureAwait(false);
            return status;
        }
        try
        {
            foreach (string peer in owned.PeerIds)
            {
                peers.Add(await transport.StageAsync(selections[peer], peer, cancellationToken).ConfigureAwait(false));
                status = status with { Peers = peers.ToArray() };
                await SaveAsync(status, false, cancellationToken).ConfigureAwait(false);
            }
            if (peers.Any(peer => peer.Phase != EUpdatePhase.Prepared || peer.Outcome != EOutcome.Succeeded))
            {
                status = status with { Outcome = EOutcome.NotExecuted, Completed = true, Reason = "update.preparationFailed" };
                await SaveAsync(status, false, cancellationToken).ConfigureAwait(false);
                return status;
            }
            await transport.StopClusterAsync(owned.OperationId, owned.PeerIds, cancellationToken).ConfigureAwait(false);
            foreach (string peer in owned.PeerIds.OrderBy(peer => string.Equals(peer, localPeerId, StringComparison.Ordinal)))
            {
                UpdatePeerResult dispatched = await transport.DispatchAsync(owned.OperationId, peer, cancellationToken).ConfigureAwait(false);
                UpdatePeerResult result = dispatched;
                if (!Terminal(dispatched))
                {
                    result = await WaitAsync(owned.OperationId, peer, owned.PeerTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                Replace(peers, result);
                status = status with { Peers = peers.ToArray() };
                await SaveAsync(status, false, cancellationToken).ConfigureAwait(false);
                if (result.Outcome != EOutcome.Succeeded && owned.FailurePolicy == EUpdateFailurePolicy.RollbackSuccessful)
                {
                    break;
                }
            }
            bool success = peers.All(peer => peer.Outcome == EOutcome.Succeeded && peer.Phase == EUpdatePhase.Completed);
            if (success)
            {
                foreach (string peer in owned.PeerIds)
                {
                    await transport.ReleaseAsync(owned.OperationId, peer, cancellationToken).ConfigureAwait(false);
                }
                status = status with { Outcome = EOutcome.Succeeded, Completed = true, Peers = peers.ToArray() };
            }
            else
            {
                await HandleFailureAsync(owned, peers, cancellationToken).ConfigureAwait(false);
                status = status with { Outcome = EOutcome.Failed, Completed = true, Peers = peers.ToArray(), Reason = "update.somePeersFailed" };
            }
            await SaveAsync(status, false, cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            string detail = exception.GetType().Name;
            if (exception is UpdateTransportException transportFailure)
            {
                detail += "." + transportFailure.Code;
            }
            status = status with { Outcome = EOutcome.Unknown, Completed = true, Peers = peers.ToArray(), Reason = "update.interruptedManualActionRequired." + detail };
            await SaveAsync(status, false, CancellationToken.None).ConfigureAwait(false);
            return status;
        }
    }

    /// <summary>既知成功の旧版復帰と不明Peerの除外を別々に扱います。</summary>
    /// <returns>事前方針の適用完了です。</returns>
    private async Task HandleFailureAsync(ClusterUpdateRequest request, List<UpdatePeerResult> peers, CancellationToken cancellationToken = default)
    {
        bool rollbackFailed = false;
        foreach (UpdatePeerResult peer in peers.ToArray())
        {
            if (request.FailurePolicy == EUpdateFailurePolicy.RollbackSuccessful && peer.Phase == EUpdatePhase.Completed && peer.Outcome == EOutcome.Succeeded)
            {
                UpdatePeerResult rollback = await transport.RollbackAsync(request.OperationId, peer.PeerId, cancellationToken).ConfigureAwait(false);
                if (!Terminal(rollback))
                {
                    rollback = await WaitAsync(rollback.OperationId, peer.PeerId, request.PeerTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                Replace(peers, rollback);
                if (rollback.Outcome != EOutcome.Succeeded)
                {
                    rollbackFailed = true;
                    await transport.ExcludeAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await transport.ReleaseAsync(request.OperationId, peer.PeerId, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                bool compatible = peer.ReleaseId is not null && (request.FailurePolicy == EUpdateFailurePolicy.RollbackSuccessful || string.Equals(peer.ReleaseId, request.Manifest.ReleaseId, StringComparison.Ordinal) || request.Manifest.CompatibleReleaseIds.Contains(peer.ReleaseId, StringComparer.Ordinal));
                bool verified = peer.Outcome != EOutcome.Unknown && peer.ReleaseId is not null && (peer.Phase == EUpdatePhase.Prepared || peer.InstanceId.HasValue);
                if (compatible && verified)
                {
                    await transport.ReleaseAsync(request.OperationId, peer.PeerId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (request.FailurePolicy == EUpdateFailurePolicy.RollbackSuccessful && peer.Phase != EUpdatePhase.Prepared)
                    {
                        rollbackFailed = true;
                    }
                    await transport.ExcludeAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        if (rollbackFailed && request.RollbackFailurePolicy == EUpdateRollbackFailurePolicy.StopCluster)
        {
            await transport.StopClusterAsync(request.OperationId, request.PeerIds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>結果不明な副作用を再送せず、有限時間だけ状態を照会します。</summary>
    /// <returns>終端状態または期限切れの結果不明です。</returns>
    private async Task<UpdatePeerResult> WaitAsync(Guid operationId, string peerId, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                UpdatePeerResult result = await transport.ReadAsync(operationId, peerId, linked.Token).ConfigureAwait(false);
                if (Terminal(result))
                {
                    return result;
                }
                await Task.Delay(100, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Interrupted, EOutcome.Unknown, Reason: "update.peerResultUnconfirmed");
    }

    /// <summary>固定Peer集合を増減せず結果だけを差し替えます。</summary>
    private static void Replace(List<UpdatePeerResult> peers, UpdatePeerResult value)
    {
        int index = peers.FindIndex(peer => string.Equals(peer.PeerId, value.PeerId, StringComparison.Ordinal));
        peers[index] = value;
    }

    /// <summary>受理と終端をphaseで分けます。</summary>
    /// <returns>終端結果であるかです。</returns>
    private static bool Terminal(UpdatePeerResult value)
    {
        return value.Phase is EUpdatePhase.Completed or EUpdatePhase.RolledBack or EUpdatePhase.Failed or EUpdatePhase.Interrupted;
    }

    /// <summary>比較保存を確認できた段階だけを次へ進めます。</summary>
    /// <returns>共有可能な操作状態の保存です。</returns>
    private async Task SaveAsync(ClusterUpdateStatus status, bool create, CancellationToken cancellationToken = default)
    {
        string key = status.Request.OperationId.ToString("N");
        DurableEntry? previous = await repository.ReadAsync("updates", key, cancellationToken).ConfigureAwait(false);
        if (create && previous is not null)
        {
            throw new IOException("更新operationは既に存在します。");
        }
        NodeValue value = NodeValue.FromJson(JsonSerializer.SerializeToElement(status, ContractJson.Options));
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new("updates", key, previous?.Version, value)], cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            throw new IOException("更新operationの保存を確認できません。");
        }
        if (localPeerId is not null)
        {
            try
            {
                IReadOnlyList<DurableMutation> notifications = await UpdateEventJournal.CreateAsync(repository, localPeerId,
                    previous?.Value?.Json.Deserialize<ClusterUpdateStatus>(ContractJson.Options), status, cancellationToken).ConfigureAwait(false);
                if (notifications.Count != 0)
                {
                    await repository.ApplyBatchAsync(Guid.CreateVersion7(), notifications, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or OperationCanceledException)
            {
                // 通知記録の障害で更新本体を失敗させません。起点Coreが元状態を照会して未記録の事象を補います。
            }
        }
    }
}
