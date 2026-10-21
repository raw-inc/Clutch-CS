using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>Host/pathの予約全体をCoordinatorで原子的に比較して共有保存します。</summary>
public sealed class ClusterFileGate : IDisposable
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly FileReservationCoordinator coordination;
    private static string AcquireKind => "file.gate.acquire";
    private static string ReleaseKind => "file.gate.release";

    /// <summary>全ファイル能力で一度だけ共通調停を接続します。</summary>
    public ClusterFileGate(PeerCluster cluster, IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.cluster = cluster;
        this.repository = repository;
        coordination = new(cluster, repository);
        cluster.RegisterDeferredHandler(AcquireKind, coordination.AcquireAsync);
        cluster.RegisterDeferredHandler(ReleaseKind, coordination.ReleaseAsync);
        cluster.RegisterDeferredHandler("file.gate.confirm", ConfirmLocalAsync);
        cluster.RegisterInspection("file.gate.probe", coordination.ProbeAsync);
        cluster.RegisterInspection(FileReservationCapabilities.Kind, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(true))));
    }

    /// <summary>競合待ちの間だけ再判定し、応答不明なら予約を再発行しません。</summary>
    /// <returns>取得確認、競合、または所有の確認不能です。</returns>
    public Task<FileLeaseResult> AcquireAsync(Guid operationId, IReadOnlyList<FileFootprint> footprints, EFileConflictPolicy policy, CancellationToken cancellationToken = default) => AcquireCoreAsync(operationId, footprints, policy, null, cancellationToken);

    /// <summary>次回の競合を待たず、現役予約にある終了済み処理だけを回収します。</summary>
    /// <returns>新しい副作用を実行しない回収結果です。</returns>
    public async Task<bool> RecoverEndedAsync(bool localOnly, CancellationToken cancellationToken = default)
    {
        if (!localOnly)
        {
            await coordination.RestoreLegacyCoordinatorAsync(cancellationToken).ConfigureAwait(false);
        }
        await coordination.MigrateAsync(localOnly, cancellationToken).ConfigureAwait(false);
        return await FileReservationRecovery.ReleaseCompletedAsync(cluster.LocalPeerId, repository, this, null, localOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>旧版への更新復帰でも保持中の排他を失わない形式へ戻します。</summary>
    /// <returns>新規調停を停止した互換保存です。</returns>
    public Task PrepareStopAsync(CancellationToken cancellationToken = default) => coordination.PrepareStopAsync(cancellationToken);

    /// <summary>上位ファイル操作の開始時集合を予約段階にも適用します。</summary>
    /// <returns>固定集合での予約結果。</returns>
    public Task<FileLeaseResult> AcquireFixedAsync(Guid operationId, IReadOnlyList<FileFootprint> footprints, EFileConflictPolicy policy, IReadOnlyList<PeerInfo> members, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        return AcquireCoreAsync(operationId, footprints, policy, members, cancellationToken);
    }

    /// <summary>競合再判定でCoordinatorや必要確認集合を差し替えません。</summary>
    /// <returns>取得確認または確認不能。</returns>
    private async Task<FileLeaseResult> AcquireCoreAsync(Guid operationId, IReadOnlyList<FileFootprint> footprints, EFileConflictPolicy policy, IReadOnlyList<PeerInfo>? required, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(footprints);
        PeerInfo[] members = required?.ToArray() ?? cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        PeerInfo local = cluster.GetPeers().Single(peer => peer.InstanceId == cluster.LocalInstanceId);
        bool localOnly = local.Status == EPeerStatus.Detached;
        PeerInfo? coordinator = members.FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal));
        if (localOnly)
        {
            members = [local];
            coordinator = local;
        }
        if (coordinator is null)
        {
            return new FileLeaseResult(EOutcome.NotExecuted, Reason: "File.CoordinatorUnavailable");
        }
        FileLease lease = new(operationId, local.PeerId, local.InstanceId, coordinator, members, ContractJson.Copy(footprints.ToArray()), localOnly);
        bool probe = false;
        bool recoveryAttempted = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string conflict = "File.Overlap";
            if (probe)
            {
                ClusterResponse available = await cluster.InspectAsync(new ClusterRequest(Guid.NewGuid(), "file.gate.probe", coordinator.PeerId, coordinator.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(lease)), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(available.Reason, conflict, StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (available.Outcome != EOutcome.Succeeded)
                {
                    return new FileLeaseResult(EOutcome.NotExecuted, Reason: "File.OverlapProbeUnavailable");
                }
            }
            lease = lease with { ReservationId = Guid.CreateVersion7(), AdmissionExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) };
            ClusterRequest request = new(lease.ReservationId, AcquireKind, coordinator.PeerId, coordinator.InstanceId, lease.AdmissionExpiresAt, NodeValue.FromObject(lease));
            ClusterResponse response = await RequestConfirmedAsync(request, cancellationToken).ConfigureAwait(false);
            FileLeaseResult result = response.Value?.Json.Deserialize<FileLeaseResult>() ?? new FileLeaseResult(response.Outcome, lease, response.Reason);
            if (!recoveryAttempted && string.Equals(result.Reason, conflict, StringComparison.Ordinal))
            {
                recoveryAttempted = true;
                if (await FileReservationRecovery.ReleaseCompletedAsync(local.PeerId, repository, this, footprints, localOnly, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
            }
            if (policy != EFileConflictPolicy.Wait || !string.Equals(result.Reason, conflict, StringComparison.Ordinal))
            {
                return result;
            }
            probe = true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>実終了を確認した呼出元だけが予約を明示解放します。</summary>
    /// <returns>解放の永続確認です。</returns>
    public async Task<FilePhaseResult> ReleaseAsync(FileLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        PeerInfo? coordinator = members.FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal));
        if (lease.LocalOnly)
        {
            coordinator = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, lease.OwnerPeerId, StringComparison.Ordinal));
            members = lease.Members.ToArray();
        }
        if (coordinator is null)
        {
            return new FilePhaseResult(EOutcome.Unknown, "File.ReleaseCoordinatorUnavailable");
        }
        ClusterRequest request = new(Guid.CreateVersion7(), ReleaseKind, coordinator.PeerId, coordinator.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(new FileGateRelease(lease, members)));
        string scope = "peer:" + cluster.LocalPeerId + ":file-gate-requests", key = request.Id.ToString("N");
        EOutcome recorded = await repository.WriteLocalSnapshotAsync(scope, key, NodeValue.FromObject(request), cancellationToken).ConfigureAwait(false);
        if (recorded != EOutcome.Succeeded)
        {
            return new FilePhaseResult(EOutcome.Unknown, "File.ReleaseRequestUnconfirmed", request.Id, coordinator.PeerId, coordinator.InstanceId);
        }
        ClusterResponse response = await RequestConfirmedAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Outcome == EOutcome.Succeeded)
        {
            // 確認済みの要求本文を重複履歴へ残さず、未確認の要求だけを復旧に必要なまま保持します。
            await repository.WriteLocalSnapshotAsync(scope, key,
                NodeValue.FromObject(new { request.Id, LeaseId = lease.Id, response.Outcome, response.Reason, CompletedAt = DateTimeOffset.UtcNow }), CancellationToken.None).ConfigureAwait(false);
        }
        return new FilePhaseResult(response.Outcome, response.Reason, request.Id, coordinator.PeerId, coordinator.InstanceId);
    }

    /// <summary>取得結果不明の元予約を、新しい予約を作らず確認します。</summary>
    /// <returns>全元参加者の永続確認。</returns>
    public async Task<FileLeaseResult> ConfirmAsync(FileLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        PeerInfo? owner = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, lease.Coordinator.PeerId, StringComparison.Ordinal));
        if (owner is null)
        {
            return new FileLeaseResult(EOutcome.Unknown, lease, "File.ReservationCoordinatorUnavailable");
        }
        ClusterRequest request = new(Guid.CreateVersion7(), "file.gate.confirm", owner.PeerId, owner.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(lease));
        ClusterResponse response = await RequestConfirmedAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Value?.Json.Deserialize<FileLeaseResult>() ?? new FileLeaseResult(EOutcome.Unknown, lease, response.Reason);
    }

    /// <summary>予約段階を一度だけ送り、配送期限を超えた結果も元要求の照会で確認します。</summary>
    /// <returns>取消時にも所有予約を呼出元へ返せる、段階の確認結果です。</returns>
    private async Task<ClusterResponse> RequestConfirmedAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
            return await RemoteNodeCompletion.ConfirmAsync(cluster, request, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(EOutcome.Unknown, Reason: "Cluster.ResponseUnconfirmed");
        }
    }

    /// <summary>元のdurableACKを確認し、取得操作を再実行しません。</summary>
    /// <returns>予約確認結果。</returns>
    private async Task<ClusterResponse> ConfirmLocalAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        FileLease lease = command.Payload.Json.Deserialize<FileLease>() ?? throw new InvalidDataException("予約がありません。");
        DurableReceipt? receipt;
        if (lease.LocalOnly)
        {
            receipt = await repository.ReadReceiptAsync(lease.ReservationId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            receipt = await cluster.ConfirmReplicationAsync(lease.ReservationId, lease.Members, cancellationToken).ConfigureAwait(false);
        }
        return Reply(new FileLeaseResult(receipt?.Outcome ?? EOutcome.Unknown, lease, receipt?.Reason));
    }

    /// <summary>受付結果を通常RPCの応答へ写します。</summary>
    /// <returns>予約確認応答。</returns>
    private static ClusterResponse Reply(FileLeaseResult result) => new(result.Outcome, NodeValue.FromObject(result), result.Reason);

    /// <summary>調停の同期資源を解放します。</summary>
    public void Dispose() => coordination.Dispose();
}
