using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>通常行と圧縮履歴を同じ内容・整合性・変更検出の規則で扱います。</summary>
internal sealed class SqliteRecordAccess(SqliteConnection connection, SqliteTransaction transaction,
    Dictionary<string, string> loaded, HashSet<string> conflictKeys, bool archived, bool trackChanges = true, bool recovering = false)
{
    private readonly SqliteHistoryReader history = new(connection, transaction, archived);
    private readonly Dictionary<Guid, DurableReceipt> verifiedReceipts = [];

    /// <summary>本文の整合性と確定結果の一致を確認した取込値を、その取引の変更検出に使用します。</summary>
    /// <returns>本文の再復元・再保存が不要な履歴です。</returns>
    internal IReadOnlyDictionary<Guid, DurableReceipt> ReuseReceipts(IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken)
    {
        IReadOnlySet<Guid> matched = MatchingReceipts(receipts, cancellationToken);
        foreach (DurableReceipt receipt in receipts)
        {
            if (matched.Contains(receipt.OperationId))
            {
                verifiedReceipts.TryAdd(receipt.OperationId, receipt);
            }
        }
        return verifiedReceipts;
    }

    /// <summary>圧縮履歴も同じ読取り取引の中でまとめて照合します。</summary>
    /// <returns>実内容が一致した操作IDです。</returns>
    internal IReadOnlySet<Guid> MatchingReceipts(IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken)
        => SqliteReceiptMatch.Read(connection, transaction, history, receipts, cancellationToken);

    /// <summary>旧形式へ戻す明示保守のため、索引に残る元の行だけを検証して渡します。</summary>
    /// <returns>一ブロック内の有効な参照です。</returns>
    internal HistoryRow[] ArchivedBlock(string id) => history.List("entry", blockId: id).Concat(history.List("receipt", blockId: id)).ToArray();

    /// <summary>通常行と圧縮履歴を同じ指紋で比較し、未変更のJSON本文を再生成しません。</summary>
    /// <returns>選択範囲に実在する検証済み項目です。</returns>
    internal IReadOnlyList<DurableObservation> Observe(string scope, IReadOnlyDictionary<string, string> known, IReadOnlySet<string>? keys, HistoryObservationCache cache, CancellationToken cancellationToken)
        => SqliteObservations.Read(connection, transaction, history, scope, known, keys, cache, cancellationToken);

    /// <summary>競合行と対応する現在行だけを読み、個別照会と一括照会で同じ候補を返します。</summary>
    /// <returns>空スコープは全用途、それ以外は指定用途の候補です。</returns>
    internal IReadOnlyList<DurableEntry> ReadConflicts(string scope, CancellationToken cancellationToken)
    {
        string? selected = scope;
        if (scope.Length == 0)
        {
            selected = null;
        }
        List<DurableEntry> result = [.. List<DurableEntry>("conflict", selected, cancellationToken: cancellationToken)];
        foreach (string address in result.Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key)).Distinct(StringComparer.Ordinal).ToArray())
        {
            DurableEntry? current = Read<DurableEntry>("entry", address);
            if (current is not null)
            {
                result.Add(current);
            }
        }
        return result.Distinct().ToArray();
    }

    /// <summary>現在行を優先し、不在の場合だけ圧縮された元の確定結果を参照します。</summary>
    /// <returns>検証済みの同じ型の値、未保存ならnullです。</returns>
    internal T? Read<T>(string kind, string key, bool remember = true)
    {
        using SqliteCommand command = new("SELECT payload,hash FROM records WHERE kind=$kind AND key=$key", connection, transaction);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        string? payload;
        if (reader.Read())
        {
            payload = reader.GetString(0);
            SqlitePayload.Verify(payload, reader.GetString(1));
        }
        else
        {
            payload = history.Read(kind, key)?.Payload;
        }
        if (payload is null)
        {
            string receipt = "receipt";
            if (string.Equals(kind, receipt, StringComparison.Ordinal) && typeof(T) == typeof(DurableReceipt) && Guid.TryParse(key, out Guid operationId))
            {
                return (T?)(object?)(SqliteHistoryGuard.Read(connection, transaction, operationId)
                    ?? (recovering ? Recovery.RecoveryRecordLedger.Read(connection, transaction, operationId)
                                   : null));
            }
            string entry = "entry";
            if (recovering && string.Equals(kind, entry, StringComparison.Ordinal))
            {
                Recovery.RecoveryRecordLedger.RequireEntry(connection, transaction, key);
            }
            return default;
        }
        if (remember)
        {
            Remember(kind, key, payload);
        }
        return JsonSerializer.Deserialize<T>(payload);
    }

    /// <summary>通常の版は操作ID索引で取得し、取込みの変更検出へ過去本文を重複保持しません。</summary>
    /// <returns>必要な親候補だけです。</returns>
    internal IReadOnlyList<DurableEntry> ReadAncestors(string scope, string key, IReadOnlyCollection<string> versions, CancellationToken cancellationToken)
        => SqliteAncestorReader.Read(connection, transaction, history, id => Read<DurableReceipt>("receipt", id.ToString("N"), remember: false), scope, key, versions, cancellationToken);

    /// <summary>通常行と圧縮済みのローカル履歴を列挙し、共有同期にはローカル履歴を混ぜません。</summary>
    /// <returns>種類・用途が一致する値です。</returns>
    internal T[] List<T>(string kind, string? scope = null, bool sharedOnly = false, bool excludeRetired = false, IReadOnlyCollection<string>? keys = null, CancellationToken cancellationToken = default)
    {
        using SqliteCommand command = new("SELECT key,payload,hash FROM records WHERE kind=$kind", connection, transaction);
        command.Parameters.AddWithValue("$kind", kind);
        if (scope is not null)
        {
            command.CommandText += " AND scope=$scope";
            command.Parameters.AddWithValue("$scope", scope);
        }
        if (sharedOnly)
        {
            command.CommandText += " AND " + SqliteSharedFingerprint.ScopeFilter;
        }
        if (excludeRetired)
        {
            command.CommandText += " AND NOT EXISTS(SELECT 1 FROM retired_keys retired WHERE retired.scope=records.scope AND retired.key=json_extract(records.payload,'$.Key'))";
        }
        HashSet<string>? remaining = null;
        if (keys is not null)
        {
            remaining = new(keys, StringComparer.Ordinal);
            command.CommandText += " AND key IN (SELECT value FROM json_each($keys))";
            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keys));
        }
        List<T> result = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = reader.GetString(0), payload = reader.GetString(1);
                remaining?.Remove(key);
                SqlitePayload.Verify(payload, reader.GetString(2));
                Remember(kind, key, payload);
                result.Add(JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("保存行が不正です。"));
            }
        }
        if (!sharedOnly && (remaining is null || remaining.Count != 0))
        {
            foreach (HistoryRow row in history.List(kind, scope, keys: remaining))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Remember(kind, row.Key, row.Payload);
                result.Add(JsonSerializer.Deserialize<T>(row.Payload) ?? throw new InvalidDataException("保存履歴が不正です。"));
            }
        }
        return result.ToArray();
    }

    /// <summary>未完了予約が参照するローカル用途だけを、本文の全走査なしで探します。</summary>
    /// <returns>既存の予約照合へ渡す元の操作結果です。</returns>
    internal IEnumerable<DurableReceipt> LocalHistoryReceipts(IReadOnlySet<string>? scopes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            yield break;
        }
        foreach (HistoryRow row in history.List("receipt", valueScopes: scopes))
        {
            loaded["receipt" + row.Key] = row.Payload;
            yield return JsonSerializer.Deserialize<DurableReceipt>(row.Payload) ?? throw new InvalidDataException("保存履歴が不正です。");
        }
    }

    /// <summary>現在値や過去の操作記録を読み込まず、項目の予約と準備内容を照合します。</summary>
    /// <returns>保存準備が残っているかです。</returns>
    internal bool HasPendingWrite(string scope, string key)
    {
        using SqliteCommand reservation = new() { Connection = connection, Transaction = transaction };
        reservation.CommandText = "SELECT operation FROM reservations WHERE address=$address";
        reservation.Parameters.AddWithValue("$address", DurableRepository.GetAddress(scope, key));
        if (reservation.ExecuteScalar() is not string operation)
        {
            return false;
        }
        PreparedBatch? pending = Read<PreparedBatch>("prepared", operation);
        if (pending is null || !Guid.TryParse(operation, out Guid id) || pending.OperationId != id
            || !pending.Mutations.Any(item => string.Equals(item.Scope, scope, StringComparison.Ordinal) && string.Equals(item.Key, key, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("保存予約と準備済みの変更内容が一致しません。");
        }
        return true;
    }

    /// <summary>同じ内容の再保存を避け、更新された行は通常形式へ戻します。</summary>
    /// <returns>実際に書込みを実行したかです。</returns>
    internal bool Write<T>(string kind, string key, string? scope, T value)
    {
        if (value is DurableReceipt confirmed && verifiedReceipts.TryGetValue(confirmed.OperationId, out DurableReceipt? original) && ReferenceEquals(confirmed, original))
        {
            return false;
        }
        string payload = JsonSerializer.Serialize(value);
        if (loaded.TryGetValue(kind + key, out string? old) && string.Equals(payload, old, StringComparison.Ordinal))
        {
            return false;
        }
        using SqliteCommand command = new("UPDATE records SET payload=$payload,hash=$hash WHERE kind=$kind AND key=$key AND scope IS $scope", connection, transaction);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$scope", (object?)scope ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payload);
        string hash = SqlitePayload.Hash(payload);
        command.Parameters.AddWithValue("$hash", hash);

        // 同じ用途の循環診断ではscope索引を組み直さず、共有変更のUPDATE triggerは維持します。
        if (command.ExecuteNonQuery() == 0)
        {
            command.CommandText = "INSERT INTO records(kind,key,scope,payload,hash) VALUES($kind,$key,$scope,$payload,$hash) ON CONFLICT(kind,key) DO UPDATE SET scope=excluded.scope,payload=excluded.payload,hash=excluded.hash";
            command.ExecuteNonQuery();
        }
        if (archived)
        {
            command.CommandText = "DELETE FROM history_index WHERE kind=$kind AND key=$key";
            command.ExecuteNonQuery();
        }
        SqliteHistoryCandidates.Enqueue(connection, transaction, kind, key, scope, DateTimeOffset.UtcNow);
        if (value is DurableReceipt receipt)
        {
            SqliteReceiptFingerprint.Save(connection, transaction, receipt, hash);
            SqliteHistoryJournal.Record(connection, transaction, receipt);
        }
        return true;
    }

    /// <summary>圧縮前後で同じ変更検出の比較元を記録します。</summary>
    private void Remember(string kind, string key, string payload)
    {
        if (!trackChanges)
        {
            return;
        }
        loaded[kind + key] = payload;
        string conflict = "conflict";
        if (string.Equals(kind, conflict, StringComparison.Ordinal))
        {
            conflictKeys.Add(key);
        }
    }
}
