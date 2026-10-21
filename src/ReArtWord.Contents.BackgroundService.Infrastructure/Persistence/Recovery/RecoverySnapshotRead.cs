using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>稼働状態と未確定・直近の結果を先に固定し、過去本文を参照せず存在情報を取得します。</summary>
internal static class RecoverySnapshotRead
{
    /// <summary>同じSQLite読取り取引で現在値と確定済み操作の識別を固定します。</summary>
    /// <returns>現在値、直近30秒と未確定の結果、および背景取得する履歴の指紋です。</returns>
    internal static RepositorySnapshot Read(string path, string peerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using SqliteSession session = new(path, write: false);
        RepositorySnapshot current = SqliteSnapshot.Export(session, includeReceipts: false);
        List<DurableReceipt> immediate = [];
        List<PersistenceOperationOwner> owners = [];
        Dictionary<(string Kind, string Key), RecoveryRecord> deferred = [];
        if (current.HistoryBoundary is not null)
        {
            HashSet<DurableEntry> selected = RepositoryStateVersion.CurrentEntries(current.Entries, true, current.ProtectedHistory ?? []).ToHashSet();
            HashSet<(string Scope, string Key)> conflicts = current.Entries.GroupBy(entry => (entry.Scope, entry.Key)).Where(group => 1 < group.Count()).Select(group => group.Key).ToHashSet();
            selected.UnionWith(current.Entries.Where(entry => entry.Value is null || conflicts.Contains((entry.Scope, entry.Key))));
            using SqliteCommand query = session.HistoryCommand();
            query.CommandText = """
                SELECT r.hash,COALESCE(c.recorded_at,0) FROM records r
                LEFT JOIN history_candidates c ON c.kind=r.kind AND c.key=r.key
                WHERE r.kind='entry' AND r.key=$key
                """;
            foreach (DurableEntry entry in current.Entries.Where(entry => !selected.Contains(entry)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = DurableRepository.GetAddress(entry.Scope, entry.Key);
                query.Parameters.Clear();
                query.Parameters.AddWithValue("$key", key);
                using SqliteDataReader row = query.ExecuteReader();
                if (!row.Read())
                {
                    throw new InvalidDataException("固定した履歴本文の検査値がありません。");
                }
                string hash = row.GetString(0);
                long recorded = HistoryRetentionPolicy.RecordedAt(entry.Value!.Json, DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(1))).ToUnixTimeSeconds();
                RecoveryEntryIdentity identity = new(entry.Scope, entry.Key, entry.Version,
                    current.CausalEntries?.FirstOrDefault(item => string.Equals(item.Scope, entry.Scope, StringComparison.Ordinal) && string.Equals(item.Key, entry.Key, StringComparison.Ordinal) && string.Equals(item.Version, entry.Version, StringComparison.Ordinal)));
                RecoveryRecord reference = new(Guid.Empty, string.Empty, EOutcome.Succeeded, null, hash, recorded, peerId, identity);
                deferred.Add((reference.Kind, reference.Key), reference);
            }
            HashSet<(string Scope, string Key, string Version)> retained = selected.Select(entry => (entry.Scope, entry.Key, entry.Version)).ToHashSet();
            current = current with { Entries = current.Entries.Where(selected.Contains).ToArray(), CausalEntries = current.CausalEntries?.Where(item => retained.Contains((item.Scope, item.Key, item.Version))).ToArray() };
        }
        using (SqliteCommand command = session.HistoryCommand())
        {
            // LEGACY-FORMAT: receipt-recovery-metadata-v1 — 補完前・旧writer更新分だけ本文へ戻り、旧writerのサポート終了後にフォールバックを撤去します。
            command.CommandText = """
                SELECT r.key,r.hash,COALESCE(f.operation_fingerprint,json_extract(r.payload,'$.Fingerprint')),
                    COALESCE(f.outcome,json_extract(r.payload,'$.Outcome')),
                    CASE WHEN f.operation_fingerprint IS NOT NULL AND f.outcome IS NOT NULL THEN f.reason ELSE json_extract(r.payload,'$.Reason') END,
                    COALESCE(c.recorded_at,0)
                FROM records r LEFT JOIN history_candidates c ON c.kind=r.kind AND c.key=r.key
                LEFT JOIN receipt_fingerprints f ON f.operation=r.key AND f.source_hash=r.hash
                WHERE r.kind='receipt' AND (r.scope='shared' OR (r.scope IS NULL AND NOT EXISTS(
                    SELECT 1 FROM json_each(r.payload,'$.Entries') e
                    WHERE substr(json_extract(e.value,'$.Scope'),1,5)='peer:'
                        OR substr(json_extract(e.value,'$.Scope'),1,9)='receipts:'
                        OR substr(json_extract(e.value,'$.Scope'),1,8)='cluster:')))
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid id = Guid.ParseExact(reader.GetString(0), "N");
                EOutcome outcome = (EOutcome)reader.GetInt32(3);
                if (!Enum.IsDefined(outcome))
                {
                    throw new InvalidDataException("保存結果の状態が不正です。");
                }
                if (outcome == EOutcome.Unknown || now.AddSeconds(-30).ToUnixTimeSeconds() <= reader.GetInt64(5))
                {
                    DurableReceipt receipt = session.Read<DurableReceipt>("receipt", id.ToString("N")) ?? throw new InvalidDataException("固定した直近履歴がありません。");
                    RoutingOwner? owner = session.Read<DurableEntry>("entry", DurableRepository.GetAddress("peer:persistence-routing", "owner:" + id.ToString("N")))?.Value?.Json.Deserialize<RoutingOwner>(ContractJson.Options);
                    PersistenceOperationOwner operation = owner?.Operation ?? new(id, receipt.Fingerprint, receipt.Entries.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal).ToArray());
                    if (operation.Scopes.All(DurableRepository.IsShared))
                    {
                        immediate.Add(receipt);
                        owners.Add(operation);
                    }
                }
                else
                {
                    string? reason = null;
                    if (!reader.IsDBNull(4))
                    {
                        reason = reader.GetString(4);
                    }
                    RecoveryRecord receipt = new(id, reader.GetString(2), outcome, reason, reader.GetString(1), reader.GetInt64(5), peerId);
                    RecoveryRecordLedger.Validate(receipt);
                    deferred.Add((receipt.Kind, receipt.Key), receipt);
                }
            }
        }
        using (SqliteCommand command = session.HistoryCommand())
        {
            command.CommandText = "SELECT payload,hash FROM recovery_history";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string payload = reader.GetString(0);
                SqlitePayload.Verify(payload, reader.GetString(1));
                RecoveryRecord receipt = JsonSerializer.Deserialize<RecoveryRecord>(payload) ?? throw new InvalidDataException("未取得履歴の識別情報がありません。");
                RecoveryRecordLedger.Validate(receipt);
                deferred.TryAdd((receipt.Kind, receipt.Key), receipt);
            }
        }
        foreach (DurableReceipt receipt in immediate)
        {
            deferred.Remove(("receipt", receipt.OperationId.ToString("N")));
        }
        return current with { Receipts = immediate, OperationOwners = owners, DeferredHistory = deferred.Values.ToArray(), DeferredSourcePeer = peerId };
    }
}
