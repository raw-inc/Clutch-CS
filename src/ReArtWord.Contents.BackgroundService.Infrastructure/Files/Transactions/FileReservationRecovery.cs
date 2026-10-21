using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>実処理の終了を永続確認できる予約だけを、所有Peerで解放し直します。</summary>
internal static class FileReservationRecovery
{
    /// <summary>実アクセス終了後も、遅れて取得が確定する可能性がある予約を先行解放しません。</summary>
    /// <returns>元予約の確定を確認してから行う解放です。実処理は再実行しません。</returns>
    internal static async Task<FilePhaseResult> ReleaseEndedAsync(ClusterFileGate gate, FileLease lease, bool reservationConfirmed, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!reservationConfirmed && (await gate.ConfirmAsync(lease, cancellationToken).ConfigureAwait(false)).Outcome != EOutcome.Succeeded)
            {
                return new(EOutcome.Unknown, "File.ReservationConfirmationPending");
            }
            return await gate.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or JsonException)
        {
            return new(EOutcome.Unknown, "File.ReleaseUnconfirmed");
        }
    }

    /// <summary>失われた元Coreに属する保存済み記録だけを、運用者の確認後に解放します。</summary>
    /// <returns>元の不明結果を成功へ書き換えない、予約だけの確認結果です。</returns>
    internal static async Task<FilePhaseResult> ReleaseConfirmedAsync(PeerCluster cluster, IDurableRepository repository, ClusterFileGate gate,
        FileReservationReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.FileAccessEndedConfirmed || request.Id == Guid.Empty || request.ExpectedOwnerInstanceId == Guid.Empty || request.ExpectedOwnerInstanceId == cluster.LocalInstanceId)
        {
            return new(EOutcome.NotExecuted, "File.AccessEndConfirmationRequired");
        }
        string scope = "peer:" + cluster.LocalPeerId + ":file-recovery";
        if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return new(EOutcome.Unknown, "File.RecoveryStateConflict");
        }
        string key = request.Id.ToString("N");
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (entry?.Value?.Kind != EValueKind.Json)
        {
            return new(EOutcome.NotExecuted, "File.RecoveryRecordMissing");
        }
        FileLease? lease = entry.Value.Json.GetProperty("lease").Deserialize<FileLease>();
        NodeResult? execution = entry.Value.Json.GetProperty("execution").Deserialize<NodeResult>();
        FilePhaseResult? previous = entry.Value.Json.GetProperty("release").Deserialize<FilePhaseResult>();
        if (lease is null || execution is null || previous is null || lease.Id != request.Id || lease.OwnerInstanceId != request.ExpectedOwnerInstanceId
            || !string.Equals(lease.OwnerPeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "File.LeaseOwnerMismatch");
        }
        if (previous.Outcome == EOutcome.Succeeded)
        {
            return previous;
        }
        FilePhaseResult release = await gate.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (release.Outcome == EOutcome.Succeeded)
        {
            NodeValue resolved = WithRelease(entry, release, true);
            DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, key, entry.Version, resolved)], cancellationToken).ConfigureAwait(false);
            if (saved.Outcome != EOutcome.Succeeded)
            {
                return new(EOutcome.Unknown, "File.RecoveryRecordUnconfirmed");
            }
        }
        return release;
    }

    /// <summary>現役予約から指定範囲または所有予約を確認し、不明な実処理や別Peerの予約には触れません。</summary>
    /// <returns>終了済み予約の解放を確認できた場合はtrueです。</returns>
    internal static async Task<bool> ReleaseCompletedAsync(string peer, IDurableRepository repository, ClusterFileGate gate,
        IReadOnlyList<FileFootprint>? footprints, bool localOnly, CancellationToken cancellationToken = default)
    {
        string scope = "peer:" + peer + ":file-recovery";
        string leaseName = "lease", executionName = "execution", releaseName = "release", keyFormat = "N";
        bool recovered = false;
        if ((await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return false;
        }
        string reservations = localOnly ? "peer:" + peer + ":file-reservations"
                                        : "file:reservations";
        string slots = reservations + ":slots";
        if ((await repository.GetConflictsAsync(reservations, cancellationToken).ConfigureAwait(false)).Count != 0
            || (await repository.GetConflictsAsync(slots, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return false;
        }
        Dictionary<Guid, FileLease> leases = (await FileReservationLedger.ReadAsync(repository, reservations, cancellationToken).ConfigureAwait(false)).Active;
        foreach (FileLease held in leases.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(held.OwnerPeerId, peer, StringComparison.Ordinal) || held.LocalOnly != localOnly
                || footprints is not null && !held.Footprints.Any(owned => footprints.Any(requested => FileOverlap.Conflicts(owned, requested))))
            {
                continue;
            }
            DurableEntry? entry = await repository.ReadAsync(scope, held.Id.ToString(keyFormat), cancellationToken).ConfigureAwait(false);
            if (entry?.Value?.Kind != EValueKind.Json || entry.Value.Json.ValueKind != JsonValueKind.Object
                || !entry.Value.Json.TryGetProperty(leaseName, out JsonElement leaseValue)
                || !entry.Value.Json.TryGetProperty(executionName, out JsonElement executionValue)
                || !entry.Value.Json.TryGetProperty(releaseName, out JsonElement releaseValue))
            {
                continue;
            }
            FileLease? lease = leaseValue.Deserialize<FileLease>();
            NodeResult? execution = executionValue.Deserialize<NodeResult>();
            FilePhaseResult? previous = releaseValue.Deserialize<FilePhaseResult>();
            bool notStarted = entry.Value.Json.TryGetProperty("fileExecutionStarted", out JsonElement started) && started.ValueKind == JsonValueKind.False;
            if (lease is null || execution is null || !notStarted && !execution.FileAccessCompleted && execution.Outcome is not (EOutcome.Succeeded or EOutcome.Failed or EOutcome.NotExecuted or EOutcome.Cancelled)
                || previous is null || previous.Outcome == EOutcome.Succeeded || lease.LocalOnly != localOnly
                || !string.Equals(lease.OwnerPeerId, peer, StringComparison.Ordinal)
                || !string.Equals(entry.Key, lease.Id.ToString(keyFormat), StringComparison.Ordinal)
                || !string.Equals(Contracts.Serialization.ContractJson.Hash(lease), Contracts.Serialization.ContractJson.Hash(held), StringComparison.Ordinal))
            {
                continue;
            }
            bool confirmed = !entry.Value.Json.TryGetProperty("reservationConfirmed", out JsonElement confirmedValue) || confirmedValue.GetBoolean();
            FilePhaseResult released = await ReleaseEndedAsync(gate, lease, confirmed, cancellationToken).ConfigureAwait(false);
            if (released.Outcome != EOutcome.Succeeded)
            {
                continue;
            }
            recovered = true;
            NodeValue resolved = WithRelease(entry, released, false);
            await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, entry.Key, entry.Version, resolved)], cancellationToken).ConfigureAwait(false);
        }
        return recovered;
    }

    /// <summary>元の実行結果・取得確認・開始状況を維持し、解放結果だけを更新します。</summary>
    /// <returns>元記録の証拠を失わない復旧状態です。</returns>
    private static NodeValue WithRelease(DurableEntry entry, FilePhaseResult release, bool accessEndedConfirmed)
    {
        Dictionary<string, JsonElement> record = entry.Value!.Json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        record["release"] = JsonSerializer.SerializeToElement(release);
        if (accessEndedConfirmed)
        {
            record["fileAccessEndedConfirmed"] = JsonSerializer.SerializeToElement(true);
        }
        return NodeValue.FromObject(record);
    }
}
