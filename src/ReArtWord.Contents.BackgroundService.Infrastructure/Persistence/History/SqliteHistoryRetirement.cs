using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>安全情報を先に残し、本文・索引・空きブロックを同じ取引で整理します。</summary>
internal static class SqliteHistoryRetirement
{
    /// <summary>共有本文を同期対象から外す前に旧祖先の索引と実際の圧縮境界を確定します。</summary>
    internal static void Compressed(SqliteSession session, IReadOnlyList<HistoryRow> rows, DateTimeOffset now)
    {
        RepositoryDocument document = new();
        SqliteHistoryGuard.Load(session, document);
        Dictionary<ERetentionCategory, DateTimeOffset> raw = [];
        foreach (HistoryRow row in rows)
        {
            if (HistoryRetentionPolicy.Category(row.Kind, row.Scope) is not { } category)
            {
                continue;
            }
            using SqliteCommand command = session.HistoryCommand();
            command.CommandText = "SELECT recorded_at FROM history_candidates WHERE kind=$kind AND key=$key";
            command.Parameters.AddWithValue("$kind", row.Kind);
            command.Parameters.AddWithValue("$key", row.Key);
            if (command.ExecuteScalar() is long recorded)
            {
                DateTimeOffset position = DateTimeOffset.FromUnixTimeSeconds(recorded).AddSeconds(1);
                if (raw.GetValueOrDefault(category) < position)
                {
                    raw[category] = position;
                }
            }
            string receiptKind = "receipt";
            if (!string.Equals(row.Kind, receiptKind, StringComparison.Ordinal) && category != ERetentionCategory.WorkflowVersions)
            {
                DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(row.Payload)!;
                if (DurableRepository.IsShared(entry.Scope))
                {
                    (document.History ??= new()).RetiredKeys.Add(DurableRepository.GetAddress(entry.Scope, entry.Key));
                }
            }
            if (string.Equals(row.Kind, receiptKind, StringComparison.Ordinal))
            {
                DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload)!;
                bool shared = HistoryReceiptScope.Shared(session, receipt);
                if (shared || receipt.Entries.Count == 0)
                {
                    (document.History ??= new()).RetiredOperations[receipt.OperationId] = new(receipt.OperationId, receipt.Fingerprint, shared, receipt.Outcome, receipt.Reason);
                }
                command.CommandText = "SELECT 1 FROM history_events WHERE operation=$key";
                if (command.ExecuteScalar() is null)
                {
                    DurableEntry[] legacy = receipt.Entries.Where(entry => DurableRepository.IsShared(entry.Scope)).ToArray();
                    SqliteCausality.Load(session, document, legacy.Select(entry => (entry.Scope, entry.Key)));
                    RepositoryLegacyAncestry.Remember(document, legacy.Select(RepositoryLegacyAncestry.Describe));
                }
            }
        }
        RepositoryHistoryGuard.Merge(document, new(raw, new Dictionary<ERetentionCategory, DateTimeOffset>(), default, now));
        session.Save(document);
    }

    /// <summary>一つの有限ブロックを処理し、混在する永久保持分も再圧縮して不要本文を残しません。</summary>
    /// <returns>削除した本文の件数です。</returns>
    internal static int DeleteOne(string path, DataRetentionSettings policy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        PruneMarkers(session);
        using SqliteCommand query = session.HistoryCommand();
        long time = HistoryRetentionPolicy.Boundary(policy, now).ToUnixTimeSeconds();
        query.CommandText = """
            SELECT i.block_id FROM retention_deadlines d JOIN history_index i ON i.kind=d.kind AND i.key=d.key
            WHERE d.delete_at<$now AND d.policy=$policy ORDER BY d.delete_at LIMIT 1
            """;
        query.Parameters.AddWithValue("$now", time);
        query.Parameters.AddWithValue("$policy", ContractJson.Hash(policy));
        if (query.ExecuteScalar() is not string block)
        {
            session.Commit();
            return 0;
        }
        HistoryRow[] rows = session.ArchivedBlock(block);
        List<HistoryRow> keep = [];
        RepositoryDocument metadata = new();
        Dictionary<ERetentionCategory, DateTimeOffset> deleted = [];
        SqliteHistoryGuard.Load(session, metadata);
        int maximumDays = Enum.GetValues<ERetentionCategory>().Where(category => !HistoryRetentionPolicy.IsLog(category)).Select(category => policy.Window(category).TotalDays ?? 0).DefaultIfEmpty(30).Max();
        DateTimeOffset operationBefore = HistoryRetentionPolicy.Boundary(policy, now).AddDays(-Math.Max(1, maximumDays));
        RepositoryHistoryGuard.Merge(metadata, new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), operationBefore, now));
        foreach (HistoryRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanDelete(session, row, time, policy, now))
            {
                keep.Add(row);
                continue;
            }
            query.Parameters.Clear();
            query.CommandText = "SELECT recorded_at FROM retention_deadlines WHERE kind=$kind AND key=$key";
            query.Parameters.AddWithValue("$kind", row.Kind);
            query.Parameters.AddWithValue("$key", row.Key);
            if (query.ExecuteScalar() is long recorded && HistoryRetentionPolicy.Category(row.Kind, row.Scope) is { } category)
            {
                DateTimeOffset position = DateTimeOffset.FromUnixTimeSeconds(recorded).AddSeconds(1);
                if (deleted.GetValueOrDefault(category) < position)
                {
                    deleted[category] = position;
                }
            }
            Retire(session, row, operationBefore);
            query.Parameters.Clear();
            query.CommandText = """
                DELETE FROM history_index WHERE kind=$kind AND key=$key;
                DELETE FROM retention_deadlines WHERE kind=$kind AND key=$key;
                DELETE FROM history_events WHERE operation=$key AND $kind='receipt';
                """;
            query.Parameters.AddWithValue("$kind", row.Kind);
            query.Parameters.AddWithValue("$key", row.Key);
            query.ExecuteNonQuery();
        }
        int removed = rows.Length - keep.Count;
        if (removed != 0)
        {
            ReplaceBlock(session, block, keep);
            RepositoryHistoryGuard.Merge(metadata, new(new Dictionary<ERetentionCategory, DateTimeOffset>(), deleted, operationBefore, now));
            session.Save(metadata);
            query.CommandText = "UPDATE repository_identity SET version=MAX(version,3)";
            query.ExecuteNonQuery();
        }
        session.Commit();
        return removed;
    }

    /// <summary>時刻付きIDの個別マーカーを単調な受付境界へ畳み込みます。</summary>
    private static void PruneMarkers(SqliteSession session)
    {
        RepositoryDocument metadata = new();
        SqliteHistoryGuard.Load(session, metadata);
        HistoryRetentionBoundary? boundary = RepositoryHistoryGuard.Read(metadata);
        if (boundary is null || boundary.OperationBefore <= DateTimeOffset.UnixEpoch)
        {
            return;
        }
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            DELETE FROM retired_operations WHERE substr(id,13,1)='7' AND substr(id,17,1) IN ('8','9','a','b') AND substr(id,1,12)<$before;
            DELETE FROM retired_keys WHERE length(key)=32 AND substr(key,13,1)='7' AND substr(key,17,1) IN ('8','9','a','b') AND substr(key,1,12)<$before;
            DELETE FROM retired_keys WHERE length(key)=38 AND substr(key,1,6)='owner:' AND substr(key,19,1)='7' AND substr(key,23,1) IN ('8','9','a','b') AND substr(key,7,12)<$before;
            """;
        command.Parameters.AddWithValue("$before", Guid.CreateVersion7(boundary.OperationBefore).ToString("N")[..12]);
        command.ExecuteNonQuery();
    }

    /// <summary>期限と現在の参照を再検証し、圧縮後に必要になった履歴を守ります。</summary>
    /// <returns>本文を削除できるかです。</returns>
    private static bool CanDelete(SqliteSession session, HistoryRow row, long time, DataRetentionSettings policy, DateTimeOffset now)
    {
        using SqliteCommand query = session.HistoryCommand();
        query.CommandText = "SELECT recorded_at FROM retention_deadlines WHERE kind=$kind AND key=$key AND delete_at<$now AND policy=$policy";
        query.Parameters.AddWithValue("$kind", row.Kind);
        query.Parameters.AddWithValue("$key", row.Key);
        query.Parameters.AddWithValue("$now", time);
        query.Parameters.AddWithValue("$policy", ContractJson.Hash(policy));
        if (query.ExecuteScalar() is not long recorded)
        {
            return false;
        }
        if (RetainedHistoryEligibility.Classify(session, row, recorded, now) is not null)
        {
            return true;
        }
        query.CommandText = "UPDATE retention_deadlines SET delete_at=$later WHERE kind=$kind AND key=$key";
        query.Parameters.AddWithValue("$later", now.AddDays(1).ToUnixTimeSeconds());
        query.ExecuteNonQuery();
        return false;
    }

    /// <summary>時刻付き操作は単調な境界、旧IDは小さい索引で再実行を拒否します。</summary>
    internal static void Retire(SqliteSession session, HistoryRow row, DateTimeOffset floor)
    {
        using SqliteCommand command = session.HistoryCommand();
        string receiptKind = "receipt";
        if (string.Equals(row.Kind, receiptKind, StringComparison.Ordinal))
        {
            DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload)!;
            if (OperationIdentity.TryGetIssuedAt(receipt.OperationId, out DateTimeOffset issued) && issued < floor)
            {
                return;
            }
            command.CommandText = "INSERT INTO retired_operations(id,fingerprint,shared,outcome,reason) VALUES($id,$fingerprint,$shared,$outcome,$reason) ON CONFLICT(id) DO NOTHING";
            command.Parameters.AddWithValue("$id", row.Key);
            command.Parameters.AddWithValue("$fingerprint", receipt.Fingerprint);
            command.Parameters.AddWithValue("$shared", HistoryReceiptScope.Shared(session, receipt));
            command.Parameters.AddWithValue("$outcome", (int)receipt.Outcome);
            command.Parameters.AddWithValue("$reason", (object?)receipt.Reason ?? DBNull.Value);
            command.ExecuteNonQuery();
            return;
        }
        DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(row.Payload)!;
        string routing = "peer:persistence-routing", ownerPrefix = "owner:";
        if (string.Equals(entry.Scope, routing, StringComparison.Ordinal) && entry.Key.StartsWith(ownerPrefix, StringComparison.Ordinal))
        {
            RoutingOwner owner = entry.Value!.Json.Deserialize<RoutingOwner>(ContractJson.Options)!;
            DurableReceipt? result = owner.Result ?? session.Read<DurableReceipt>("receipt", owner.Operation.OperationId.ToString("N"));
            if (result is not null && result.Outcome != EOutcome.Unknown && result.Entries.Count == 0)
            {
                RepositoryDocument ownership = new() { History = new() };
                ownership.History.RetiredOperations[result.OperationId] = new(result.OperationId, owner.Operation.Fingerprint,
                    owner.Operation.Scopes.All(DurableRepository.IsShared), result.Outcome, result.Reason);
                SqliteHistoryGuard.Save(session, ownership);
            }
        }
        command.CommandText = "DELETE FROM records WHERE kind='entry' AND key IN ($causal,$legacy)";
        command.Parameters.AddWithValue("$causal", DurableRepository.GetAddress(RepositoryCausality.Scope, RepositoryCausality.EntryKey(entry.Scope, entry.Key)));
        command.Parameters.AddWithValue("$legacy", DurableRepository.GetAddress(RepositoryCausality.Scope, "legacy:" + DurableRepository.GetAddress(entry.Scope, entry.Key)));
        command.ExecuteNonQuery();
        if (HistoryRetentionPolicy.Category(row.Kind, row.Scope) is ERetentionCategory.RequestHistory or ERetentionCategory.OperationHistory or ERetentionCategory.ExecutionHistory or ERetentionCategory.Snapshots)
        {
            if (RepositoryHistoryGuard.TryOperationId(entry.Key, out Guid id) && OperationIdentity.TryGetIssuedAt(id, out DateTimeOffset issued) && issued < floor)
            {
                return;
            }
            command.CommandText = "INSERT INTO retired_keys(scope,key) VALUES($scope,$key) ON CONFLICT(scope,key) DO NOTHING";
            command.Parameters.AddWithValue("$scope", entry.Scope);
            command.Parameters.AddWithValue("$key", entry.Key);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>参照が残る本文だけで同じブロックを作り直します。</summary>
    private static void ReplaceBlock(SqliteSession session, string id, List<HistoryRow> rows)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.Parameters.AddWithValue("$id", id);
        if (rows.Count == 0)
        {
            command.CommandText = "DELETE FROM history_blocks WHERE id=$id";
            command.ExecuteNonQuery();
            return;
        }
        (byte[] data, int length, string hash) = HistoryBlockCodec.Encode(rows);
        command.CommandText = "UPDATE history_blocks SET data=$data,raw_length=$length,hash=$hash,records=$count WHERE id=$id";
        command.Parameters.AddWithValue("$data", data);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$count", rows.Count);
        command.ExecuteNonQuery();
        for (int index = 0; index < rows.Count; index++)
        {
            command.CommandText = "UPDATE history_index SET ordinal=$ordinal WHERE block_id=$id AND kind=$kind AND key=$key";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$kind", rows[index].Kind);
            command.Parameters.AddWithValue("$key", rows[index].Key);
            command.ExecuteNonQuery();
        }
    }
}
