using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>JSON保存でも同じ確定イベント数と基準点の保持条件を適用します。</summary>
internal static class JsonHistoryCheckpoints
{
    /// <summary>一取引で新たに確定した操作だけを数えます。</summary>
    internal static void Record(RepositoryDocument document, IReadOnlySet<Guid> previous, DateTimeOffset now)
    {
        RepositoryHistory? history = document.History;
        if (history is null)
        {
            return;
        }
        string receiptKind = "receipt", idFormat = "N";
        foreach (DurableReceipt receipt in document.Receipts.Values.Where(receipt => !previous.Contains(receipt.OperationId)))
        {
            if (receipt.Outcome != EOutcome.Succeeded || receipt.Entries.Count == 0 || receipt.Entries.Any(entry => !DurableRepository.IsShared(entry.Scope))
                || RepositoryCausality.Export(document, receipt.Entries).Length != receipt.Entries.Count || history.Events.ContainsKey(receipt.OperationId)
                || history.Archives.ContainsKey(JsonHistoryAccess.Address(receiptKind, receipt.OperationId.ToString(idFormat))))
            {
                continue;
            }
            history.Sequence = checked(history.Sequence + 1);
            history.Events[receipt.OperationId] = new(history.Sequence, now);
        }
    }

    /// <summary>初回基準点の後は変更がある場合だけ件数または時間で取得します。</summary>
    internal static void Capture(RepositoryDocument document, DataRetentionSettings policy, DateTimeOffset now)
    {
        RepositoryHistory history = document.History!;
        JsonHistoryCheckpoint? last = history.Checkpoints.LastOrDefault();
        if (!RepositoryCheckpointRequest.Required(document) && last is not null && (history.Sequence <= last.Info.Sequence || history.Sequence - last.Info.Sequence < policy.CheckpointEvents
            && now < last.Info.CreatedAt.AddDays(policy.CheckpointDays)))
        {
            return;
        }
        DurableEntry[] current = document.Entries.Values.Concat(document.Conflicts).Where(entry => DurableRepository.IsShared(entry.Scope)
            && HistoryRetentionPolicy.Category("entry", entry.Scope) is null).Distinct().ToArray();
        if (current.Length == 0)
        {
            return;
        }
        RepositorySnapshot snapshot = new(current, [], CausalEntries: RepositoryCausality.Export(document, current), LegacyEntries: RepositoryLegacyAncestry.Export(document, current),
            HistoryBoundary: RepositoryHistoryGuard.Read(document), RetiredOperations: history.RetiredOperations.Values.Where(operation => operation.Shared).ToArray());
        byte[] payload = HistoryCheckpointCodec.Encode(snapshot);
        HistoryCheckpointInfo info = new(Guid.CreateVersion7(now).ToString("N"), history.Sequence, now, false, payload.Length, payload.Length);
        DateTimeOffset? expires = null;
        if (policy.Window(ERetentionCategory.Snapshots).TotalDays is int days)
        {
            expires = now.AddDays(days);
        }
        history.Checkpoints.Add(new(info, payload, HistoryCheckpointCodec.Hash(payload), expires));
        RepositoryCheckpointRequest.Set(document, false);
    }

    /// <summary>基準点自体も圧縮・削除し、直近二世代だけは復旧のため維持します。</summary>
    internal static void Maintain(RepositoryHistory history, DataRetentionSettings policy, DateTimeOffset now)
    {
        RetentionWindow retention = policy.Window(ERetentionCategory.Snapshots);
        DateTimeOffset boundary = HistoryRetentionPolicy.Boundary(policy, now);
        HashSet<string> latest = history.Checkpoints.TakeLast(2).Select(checkpoint => checkpoint.Info.Id).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < history.Checkpoints.Count; index++)
        {
            JsonHistoryCheckpoint checkpoint = history.Checkpoints[index];
            DateTimeOffset? expires = null;
            if (retention.TotalDays is int days)
            {
                expires = checkpoint.Info.CreatedAt.AddDays(days);
            }
            if (!policy.ApplyToExisting && (checkpoint.DeleteAt is null || expires < checkpoint.DeleteAt))
            {
                expires = checkpoint.DeleteAt;
            }
            history.Checkpoints[index] = checkpoint with { DeleteAt = expires };
        }
        history.Checkpoints.RemoveAll(checkpoint => !latest.Contains(checkpoint.Info.Id) && checkpoint.DeleteAt < boundary);
        for (int index = 0; index < history.Checkpoints.Count; index++)
        {
            JsonHistoryCheckpoint checkpoint = history.Checkpoints[index];
            if (!checkpoint.Info.Compressed && checkpoint.Info.CreatedAt < boundary.AddDays(-retention.RawDays))
            {
                _ = HistoryCheckpointCodec.Decode(checkpoint.Payload, checked((int)checkpoint.Info.RawBytes), checkpoint.Hash, false);
                byte[] compressed = HistoryCheckpointCodec.Compress(checkpoint.Payload);
                history.Checkpoints[index] = checkpoint with { Payload = compressed, Info = checkpoint.Info with { Compressed = true, StoredBytes = compressed.Length } };
                break;
            }
        }
    }
}
