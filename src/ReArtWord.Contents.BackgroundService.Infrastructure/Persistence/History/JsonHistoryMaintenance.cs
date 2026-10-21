using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>JSON保存先では履歴本文も同じ文書置換へ含め、未完了の別ファイルを公開しません。</summary>
internal static class JsonHistoryMaintenance
{
    /// <summary>保持境界、期限、本文の圧縮・削除を一回の原子的保存で確定します。</summary>
    /// <returns>削除した本文数です。</returns>
    internal static int Run(RepositoryDocument document, DataRetentionSettings policy, DateTimeOffset now, int maximum)
    {
        RepositoryHistory history = document.History ??= new();
        JsonHistoryCheckpoints.Capture(document, policy, now);
        IEnumerable<HistoryRow> rows = document.Receipts.Values.ToArray().Select(receipt => Row("receipt", receipt.OperationId.ToString("N"), SqliteReceipts.Scope(receipt), receipt))
            .Concat(document.Entries.Values.Where(entry => HistoryRetentionPolicy.Category("entry", entry.Scope) is not null).ToArray()
                .Select(entry => Row("entry", DurableRepository.GetAddress(entry.Scope, entry.Key), entry.Scope, entry)));
        int processed = 0;
        Dictionary<ERetentionCategory, DateTimeOffset> raw = [];
        Dictionary<ERetentionCategory, DateTimeOffset> deletedHot = [];
        int hotRemoved = 0;
        int maximumDays = Enum.GetValues<ERetentionCategory>().Where(category => !HistoryRetentionPolicy.IsLog(category)).Max(category => policy.Window(category).TotalDays ?? 0);
        DateTimeOffset floor = HistoryRetentionPolicy.Boundary(policy, now).AddDays(-Math.Max(1, maximumDays));
        foreach (HistoryRow row in rows)
        {
            string address = JsonHistoryAccess.Address(row.Kind, row.Key);
            HistoryDeadline? previous = history.Deadlines.GetValueOrDefault(address);
            DateTimeOffset observed = previous?.RecordedAt ?? now;
            if (previous is null && Guid.TryParse(row.Key, out Guid operationId) && history.Events.TryGetValue(operationId, out HistoryEventPosition? position))
            {
                observed = position.RecordedAt;
            }
            DateTimeOffset? date = JsonHistoryEligibility.Recorded(document, row, observed, now);
            if (date is null)
            {
                continue;
            }
            HistoryDeadline deadline = Deadline(policy, HistoryRetentionPolicy.Category(row.Kind, row.Scope)!.Value, date.Value, previous);
            history.Deadlines[address] = deadline;
            bool legacy = LegacyHistoryFormat.Detect(row.Payload) is not null;
            if (!legacy && HistoryRetentionPolicy.Boundary(policy, now).AddDays(-policy.Window(deadline.Category).RawDays) < date.Value || now < date.Value.AddMinutes(10))
            {
                continue;
            }
            bool tooLarge = HistoryBlockCodec.MaximumBytes < HistoryBlockCodec.EstimatedBytes(row) + 2;
            if (tooLarge && (deadline.DeleteAt is null || HistoryRetentionPolicy.Boundary(policy, now) <= deadline.DeleteAt))
            {
                continue;
            }
            if (!tooLarge)
            {
                (byte[] data, int length, string hash) = HistoryBlockCodec.Encode([row]);
                if (!HistoryBlockCodec.Decode(data, length, hash).SequenceEqual([row]))
                {
                    throw new InvalidDataException("移行先の履歴本文が元の内容と一致しません。");
                }
                history.Archives[address] = new(row.Kind, row.Key, row.Scope, data, length, hash);
            }
            string receiptKind = "receipt";
            if (string.Equals(row.Kind, receiptKind, StringComparison.Ordinal))
            {
                DurableReceipt receipt = document.Receipts[Guid.Parse(row.Key)];
                bool shared = HistoryReceiptScope.Shared(document, receipt);
                if (shared || receipt.Entries.Count == 0)
                {
                    history.RetiredOperations.TryAdd(receipt.OperationId, new(receipt.OperationId, receipt.Fingerprint, shared, receipt.Outcome, receipt.Reason));
                }
                if (!history.Events.ContainsKey(receipt.OperationId))
                {
                    RepositoryLegacyAncestry.Remember(document, receipt.Entries.Where(entry => DurableRepository.IsShared(entry.Scope)).Select(RepositoryLegacyAncestry.Describe));
                }
                document.Receipts.Remove(receipt.OperationId);
            }
            else
            {
                DurableEntry entry = document.Entries[row.Key];
                if (DurableRepository.IsShared(entry.Scope) && deadline.Category != ERetentionCategory.WorkflowVersions)
                {
                    history.RetiredKeys.Add(row.Key);
                }
                document.Entries.Remove(row.Key);
            }
            if (raw.GetValueOrDefault(deadline.Category) < date.Value.AddSeconds(1))
            {
                raw[deadline.Category] = date.Value.AddSeconds(1);
            }
            if (tooLarge)
            {
                Retire(document, row, deadline.Category, floor);
                history.Deadlines.Remove(address);
                deletedHot[deadline.Category] = date.Value.AddSeconds(1);
                hotRemoved++;
            }
            if (maximum <= ++processed)
            {
                break;
            }
        }
        if (raw.Count != 0)
        {
            RepositoryHistoryGuard.Merge(document, new(raw, new Dictionary<ERetentionCategory, DateTimeOffset>(), default, now));
        }
        if (hotRemoved != 0)
        {
            RepositoryHistoryGuard.Merge(document, new(raw, deletedHot, floor, now));
        }
        int deleted = Delete(document, policy, now, maximum);
        JsonHistoryCheckpoints.Maintain(history, policy, now);
        return deleted + hotRemoved;
    }

    /// <summary>圧縮済みの一件を検証してから削除します。</summary>
    /// <returns>今回の削除件数です。</returns>
    private static int Delete(RepositoryDocument document, DataRetentionSettings policy, DateTimeOffset now, int maximum)
    {
        RepositoryHistory history = document.History!;
        DateTimeOffset boundary = HistoryRetentionPolicy.Boundary(policy, now);
        int maximumDays = Enum.GetValues<ERetentionCategory>().Where(category => !HistoryRetentionPolicy.IsLog(category)).Max(category => policy.Window(category).TotalDays ?? 0);
        DateTimeOffset floor = boundary.AddDays(-Math.Max(1, maximumDays));
        int deleted = 0;
        Dictionary<ERetentionCategory, DateTimeOffset> deletedBefore = [];
        foreach ((string address, JsonHistoryRecord record) in history.Archives.ToArray())
        {
            HistoryDeadline old = history.Deadlines[address];
            HistoryDeadline deadline = Deadline(policy, old.Category, old.RecordedAt, old);
            history.Deadlines[address] = deadline;
            if (deadline.DeleteAt is null || boundary <= deadline.DeleteAt.Value)
            {
                continue;
            }
            HistoryRow row = HistoryBlockCodec.Decode(record.Data, record.Length, record.Hash).Single();
            if (JsonHistoryEligibility.Recorded(document, row, deadline.RecordedAt, now) is null)
            {
                continue;
            }
            Retire(document, row, deadline.Category, floor);
            if (deletedBefore.GetValueOrDefault(deadline.Category) < deadline.RecordedAt.AddSeconds(1))
            {
                deletedBefore[deadline.Category] = deadline.RecordedAt.AddSeconds(1);
            }
            history.Archives.Remove(address);
            history.Deadlines.Remove(address);
            if (maximum <= ++deleted)
            {
                break;
            }
        }
        if (deleted != 0)
        {
            RepositoryHistoryGuard.Merge(document, new(new Dictionary<ERetentionCategory, DateTimeOffset>(), deletedBefore, floor, now));
        }
        DateTimeOffset retiredBefore = RepositoryHistoryGuard.Read(document)?.OperationBefore ?? default;
        foreach (Guid id in history.RetiredOperations.Keys.Where(id => OperationIdentity.TryGetIssuedAt(id, out DateTimeOffset issued) && issued < retiredBefore).ToArray())
        {
            history.RetiredOperations.Remove(id);
        }
        history.RetiredKeys.RemoveWhere(address => RepositoryHistoryGuard.TryOperationId(JsonSerializer.Deserialize<string[]>(address)![1], out Guid id)
            && OperationIdentity.TryGetIssuedAt(id, out DateTimeOffset issued) && issued < retiredBefore);
        return deleted;
    }

    /// <summary>新しい期限を既存記録へ適用する範囲を守ります。</summary>
    /// <returns>この記録の有効な期限です。</returns>
    private static HistoryDeadline Deadline(DataRetentionSettings policy, ERetentionCategory category, DateTimeOffset recorded, HistoryDeadline? old)
    {
        DateTimeOffset? expires = null;
        if (policy.Window(category).TotalDays is int days)
        {
            expires = recorded.AddDays(days);
        }
        if (old is not null && !policy.ApplyToExisting && (old.DeleteAt is null || expires < old.DeleteAt))
        {
            expires = old.DeleteAt;
        }
        return new(category, recorded, expires);
    }

    /// <summary>本文を持たずに旧IDの再使用だけを拒否します。</summary>
    private static void Retire(RepositoryDocument document, HistoryRow row, ERetentionCategory category, DateTimeOffset floor)
    {
        RepositoryHistory history = document.History!;
        string receiptKind = "receipt";
        if (string.Equals(row.Kind, receiptKind, StringComparison.Ordinal))
        {
            DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload)!;
            history.Events.Remove(receipt.OperationId);
            if (!OperationIdentity.TryGetIssuedAt(receipt.OperationId, out DateTimeOffset issued) || floor <= issued)
            {
                history.RetiredOperations.TryAdd(receipt.OperationId, new(receipt.OperationId, receipt.Fingerprint, HistoryReceiptScope.Shared(document, receipt), receipt.Outcome, receipt.Reason));
            }
        }
        else
        {
            DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(row.Payload)!;
            string routing = "peer:persistence-routing", ownerPrefix = "owner:";
            if (string.Equals(entry.Scope, routing, StringComparison.Ordinal) && entry.Key.StartsWith(ownerPrefix, StringComparison.Ordinal))
            {
                RoutingOwner owner = entry.Value!.Json.Deserialize<RoutingOwner>(Contracts.Serialization.ContractJson.Options)!;
                DurableReceipt? result = owner.Result ?? JsonHistoryAccess.Receipt(document, owner.Operation.OperationId);
                if (result is not null && result.Outcome != EOutcome.Unknown && result.Entries.Count == 0)
                {
                    history.RetiredOperations.TryAdd(result.OperationId, new(result.OperationId, owner.Operation.Fingerprint,
                        owner.Operation.Scopes.All(DurableRepository.IsShared), result.Outcome, result.Reason));
                }
            }
            if (category is ERetentionCategory.RequestHistory or ERetentionCategory.OperationHistory or ERetentionCategory.ExecutionHistory or ERetentionCategory.Snapshots && (!RepositoryHistoryGuard.TryOperationId(entry.Key, out Guid id)
                || !OperationIdentity.TryGetIssuedAt(id, out DateTimeOffset issued) || floor <= issued))
            {
                history.RetiredKeys.Add(row.Key);
            }
            document.Entries.Remove(DurableRepository.GetAddress(RepositoryCausality.Scope, RepositoryCausality.EntryKey(entry.Scope, entry.Key)));
            document.Entries.Remove(DurableRepository.GetAddress(RepositoryCausality.Scope, "legacy:" + DurableRepository.GetAddress(entry.Scope, entry.Key)));
        }
    }

    /// <summary>同じ本文検証器へJSON保存行を渡します。</summary>
    /// <returns>変更されない一行です。</returns>
    private static HistoryRow Row<T>(string kind, string key, string? scope, T value)
    {
        string payload = JsonSerializer.Serialize(value);
        return new(kind, key, scope, payload, SqlitePayload.Hash(payload));
    }
}
