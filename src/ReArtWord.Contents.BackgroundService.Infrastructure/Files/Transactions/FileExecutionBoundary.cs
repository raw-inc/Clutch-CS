using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>Keep既定のFile・AI・mediaに同じクラスタファイル予約を適用します。</summary>
internal sealed class FileExecutionBoundary(PeerCluster cluster, IDurableRepository repository, ClusterFileGate gate, FileNodeEndpoint endpoint, FileStageClient client, EFileConflictPolicy policy)
{
    /// <summary>準備済み予約を引継ぐか、実操作の前後に共通予約を取得・解放します。</summary>
    /// <returns>元の実処理結果と必要な復旧情報。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(IActionNode node, NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        string? problem = await endpoint.StartProblemAsync(node, context, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return NodeResult.Failure("File.DependencyUnavailable", problem, EOutcome.NotExecuted);
        }
        if (node is not IFileFootprintNode file)
        {
            return await node.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        FileLease? lease = null;
        bool reservationConfirmed = false;
        bool executed = false;
        NodeResult result;
        try
        {
            PreparedFileOperation? prepared = await endpoint.ReadPreparedAsync(context.ExecutionId, cancellationToken).ConfigureAwait(false);
            if (prepared is not null)
            {
                if (prepared.Released || prepared.Token is null || !string.Equals(prepared.Request.Revision, context.Revision, StringComparison.Ordinal) || !string.Equals(ContractJson.Hash(prepared.Request.Operational), ContractJson.Hash(context.Operational), StringComparison.Ordinal) || !string.Equals(ContractJson.Hash(prepared.Request.Inputs), ContractJson.Hash(context.Inputs), StringComparison.Ordinal) || !string.Equals(ContractJson.Hash(prepared.Request.Node.Settings), ContractJson.Hash(context.Settings), StringComparison.Ordinal))
                {
                    return NodeResult.Failure("File.PreparedExecutionMismatch", "保持中の全対象予約と実行内容が一致しません。", EOutcome.NotExecuted);
                }
                IReadOnlyList<FileFootprint> verified = await ResolveAsync(await file.GetFileAccessAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                if (!verified.All(prepared.Lease.Footprints.Contains))
                {
                    return NodeResult.Failure("File.PreparedScopeChanged", "準備時の所有実体またはファイル範囲が変わりました。", EOutcome.NotExecuted);
                }
                executed = true;
                return await node.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<FilePathAccess> paths = await file.GetFileAccessAsync(context, cancellationToken).ConfigureAwait(false);
            if (paths.Count == 0)
            {
                executed = true;
                return await node.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<FileFootprint> scopes = await ResolveAsync(paths, cancellationToken).ConfigureAwait(false);
            FileLeaseResult acquired = await gate.AcquireAsync(context.ExecutionId, scopes, context.Operational?.Settings.FileConflictPolicy ?? policy, cancellationToken).ConfigureAwait(false);
            lease = acquired.Lease;
            reservationConfirmed = acquired.Outcome == EOutcome.Succeeded;
            if (acquired.Outcome != EOutcome.Succeeded)
            {
                result = NodeResult.Failure(acquired.Reason ?? "File.ReservationUnconfirmed", "ファイル予約の取得を確認できません。", acquired.Outcome);
            }
            else
            {
                IReadOnlyList<FileFootprint> verified = await ResolveAsync(await file.GetFileAccessAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                if (!verified.All(lease!.Footprints.Contains))
                {
                    throw new IOException("予約後に所有実体またはファイル範囲が変わりました。");
                }
                executed = true;
                result = await node.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (executed)
            {
                outcome = EOutcome.Unknown;
            }
            result = NodeResult.Failure("File.ExecutionUnconfirmed", "ファイルの許可・予約・実処理を確認できません。", outcome);
        }
        if (lease is not null)
        {
            FilePhaseResult released = new(EOutcome.NotExecuted, "File.ReservationRetained");
            if (!executed || result.Outcome != EOutcome.Unknown || result.FileAccessCompleted)
            {
                released = await FileReservationRecovery.ReleaseEndedAsync(gate, lease, reservationConfirmed, CancellationToken.None).ConfigureAwait(false);
            }
            if (released.Outcome != EOutcome.Succeeded)
            {
                NodeValue recovery = NodeValue.FromObject(new { lease, execution = result, release = released, reservationConfirmed, fileExecutionStarted = executed });
                await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation("peer:" + cluster.LocalPeerId + ":file-recovery", context.ExecutionId.ToString("N"), null, recovery)], CancellationToken.None).ConfigureAwait(false);
                Dictionary<string, NodeValue> outputs = result.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                outputs["fileReservation"] = recovery;
                result = result with { Outputs = outputs };
            }
        }
        return result;
    }

    /// <summary>remoteのパスはその所有Peerに許可検査させます。</summary>
    /// <returns>Hostとパスを確定した範囲。</returns>
    public Task<IReadOnlyList<FileFootprint>> ResolveAsync(IReadOnlyList<FilePathAccess> paths, CancellationToken cancellationToken = default) => ResolveCoreAsync(paths, cancellationToken);

    /// <summary>全所有Peerから正規化済み範囲を集めます。</summary>
    /// <returns>実ファイルの範囲。</returns>
    private async Task<IReadOnlyList<FileFootprint>> ResolveCoreAsync(IReadOnlyList<FilePathAccess> paths, CancellationToken cancellationToken = default)
    {
        List<FileFootprint> footprints = [];
        foreach (FilePathAccess path in paths)
        {
            if (string.Equals(path.PeerId, cluster.LocalPeerId, StringComparison.Ordinal))
            {
                footprints.Add(endpoint.Resolve(path));
                continue;
            }
            if (cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && peer.Status == EPeerStatus.Detached))
            {
                throw new IOException("明示離脱中は別Peerのファイルを操作できません。");
            }
            PeerInfo peer = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, path.PeerId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status)) ?? throw new IOException("ファイル所有Peerを確認できません。");
            ClusterResponse response = await client.InspectAsync("file.transaction.path", peer, NodeValue.FromObject(path), cancellationToken).ConfigureAwait(false);
            FileFootprint? footprint = response.Value?.Json.Deserialize<FileFootprint>();
            if (response.Outcome != EOutcome.Succeeded || footprint is null || footprint.InstanceId != peer.InstanceId || !string.Equals(footprint.HostId, peer.HostId, StringComparison.Ordinal))
            {
                throw new IOException("ファイル所有Peerの許可検査を確認できません。");
            }
            footprints.Add(footprint);
        }
        return footprints;
    }
}
