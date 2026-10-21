using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>本文未取得の存在証明を現在値と同じ取引で保存し、再起動後も再実行を拒否します。</summary>
internal static class RecoveryRecordLedger
{
    internal static string PendingReason => "Persistence.HistoryPending";

    /// <summary>先行同期と存在証明を同じ保存取引で確定します。</summary>
    internal static void RegisterSnapshot(SqliteSession session, Documents.RepositorySnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.DeferredHistory is { } deferred)
        {
            Register(session, deferred, snapshot.DeferredSourcePeer ?? throw new InvalidDataException("背景履歴の送信元がありません。"), cancellationToken);
        }
        else
        {
            RemovePresent(session);
            RefreshCompatibility(session);
        }
    }

    /// <summary>未取得の存在証明を理解しない旧writerが起動しない保存境界を用意します。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            CREATE TABLE IF NOT EXISTS recovery_history(
                kind TEXT NOT NULL,key TEXT NOT NULL,payload TEXT NOT NULL,hash TEXT NOT NULL,
                source_peer TEXT NOT NULL,problem TEXT,PRIMARY KEY(kind,key));
            CREATE TABLE IF NOT EXISTS recovery_progress(id INTEGER PRIMARY KEY CHECK(id=1),total INTEGER NOT NULL);
            INSERT INTO recovery_progress(id,total) VALUES(1,0) ON CONFLICT(id) DO NOTHING;
            CREATE TABLE IF NOT EXISTS recovery_verified(
                kind TEXT NOT NULL,key TEXT NOT NULL,remote_hash TEXT NOT NULL,local_hash TEXT NOT NULL,
                PRIMARY KEY(kind,key,remote_hash));
            CREATE TRIGGER IF NOT EXISTS recovery_verified_changed AFTER UPDATE OF payload,hash ON records
                WHEN OLD.hash<>NEW.hash OR OLD.payload<>NEW.payload BEGIN
                DELETE FROM recovery_verified WHERE kind=OLD.kind AND key=OLD.key AND local_hash<>NEW.hash; END;
            CREATE TRIGGER IF NOT EXISTS recovery_verified_record_deleted AFTER DELETE ON records BEGIN
                DELETE FROM recovery_verified WHERE kind=OLD.kind AND key=OLD.key; END;
            CREATE TRIGGER IF NOT EXISTS recovery_verified_archive_deleted AFTER DELETE ON history_index BEGIN
                DELETE FROM recovery_verified WHERE kind=OLD.kind AND key=OLD.key
                AND NOT EXISTS(SELECT 1 FROM records WHERE kind=OLD.kind AND key=OLD.key); END;
            """, connection, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>通常本文が存在しない操作の存在だけを返し、成功や未実行を推測しません。</summary>
    /// <returns>未取得ならUnknown、未登録ならnullです。</returns>
    internal static DurableReceipt? Read(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using SqliteCommand command = new("SELECT payload,hash FROM recovery_history WHERE kind='receipt' AND key=$id", connection, transaction);
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        string payload = reader.GetString(0);
        SqlitePayload.Verify(payload, reader.GetString(1));
        RecoveryRecord receipt = JsonSerializer.Deserialize<RecoveryRecord>(payload) ?? throw new InvalidDataException("未取得履歴の識別情報がありません。");
        Validate(receipt);
        if (receipt.OperationId != id)
        {
            throw new InvalidDataException("未取得履歴の操作識別が一致しません。");
        }
        return new(id, receipt.Fingerprint, EOutcome.Unknown, [], PendingReason);
    }

    /// <summary>存在する履歴の未取得を、未保存のnullとして返しません。</summary>
    internal static void RequireEntry(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using SqliteCommand command = new("SELECT payload,hash FROM recovery_history WHERE kind='entry' AND key=$key", connection, transaction);
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            string payload = reader.GetString(0);
            SqlitePayload.Verify(payload, reader.GetString(1));
            RecoveryRecord reference = JsonSerializer.Deserialize<RecoveryRecord>(payload) ?? throw new InvalidDataException("未取得履歴の識別が不正です。");
            Validate(reference);
            if (reference.Entry is null || !string.Equals(reference.Key, key, StringComparison.Ordinal))
            {
                throw new InvalidDataException("未取得履歴の識別が一致しません。");
            }
            throw new IOException(PendingReason);
        }
    }

    /// <summary>前景の再送でも、元の存在証明と同じ本文だけを未取得状態から確定します。</summary>
    internal static void ValidateIncoming(SqliteSession session, DurableReceipt receipt)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT payload,hash FROM recovery_history WHERE kind='receipt' AND key=$id";
        command.Parameters.AddWithValue("$id", receipt.OperationId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("未取得履歴の存在証明がありません。");
        }
        string payload = reader.GetString(0);
        SqlitePayload.Verify(payload, reader.GetString(1));
        RecoveryRecord reference = JsonSerializer.Deserialize<RecoveryRecord>(payload) ?? throw new InvalidDataException("未取得履歴の存在証明が不正です。");
        Validate(reference);
        if (reference.OperationId != receipt.OperationId || !string.Equals(reference.Hash, SqlitePayload.Hash(JsonSerializer.Serialize(receipt)), StringComparison.Ordinal))
        {
            throw new InvalidDataException("未取得履歴の存在証明と本文が一致しません。");
        }
    }

    /// <summary>同じ本文が既にあれば書かず、異なる表現・未取得だけを背景照合へ渡します。</summary>
    internal static void Register(SqliteSession session, IReadOnlyList<RecoveryRecord> receipts, string sourcePeer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePeer);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT count(*) FROM recovery_history";
        long before = (long)command.ExecuteScalar()!;
        RemovePresent(session);
        RepositoryDocument retention = new();
        SqliteHistoryGuard.Load(session, retention);
        HistoryRetentionBoundary? boundary = RepositoryHistoryGuard.Read(retention);
        Dictionary<Guid, RetiredOperation> retired = SqliteHistoryGuard.Export(session).ToDictionary(operation => operation.Id);
        long added = 0;
        foreach (RecoveryRecord receipt in receipts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(receipt);
            string key = receipt.Key, payload = JsonSerializer.Serialize(receipt);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", key);
            command.Parameters.AddWithValue("$kind", receipt.Kind);
            command.CommandText = "SELECT hash FROM records WHERE kind=$kind AND key=$id UNION ALL SELECT hash FROM history_index WHERE kind=$kind AND key=$id LIMIT 1";
            string? localHash = command.ExecuteScalar() as string;
            bool verified = string.Equals(localHash, receipt.Hash, StringComparison.OrdinalIgnoreCase);
            if (!verified && localHash is not null)
            {
                command.CommandText = "SELECT 1 FROM recovery_verified WHERE kind=$kind AND key=$id AND remote_hash=$remote AND local_hash=$local";
                command.Parameters.AddWithValue("$remote", receipt.Hash);
                command.Parameters.AddWithValue("$local", localHash);
                verified = command.ExecuteScalar() is not null;
            }
            if (localHash is null)
            {
                if (receipt.Entry is null)
                {
                    DurableReceipt? expired = RepositoryHistoryGuard.Expired(receipt.OperationId, boundary, retired.GetValueOrDefault(receipt.OperationId));
                    if (expired is not null && expired.Fingerprint.Length != 0 && !string.Equals(expired.Fingerprint, receipt.Fingerprint, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("削除済み操作と先行同期の指紋が一致しません。");
                    }
                    verified = expired is not null;
                }
                else
                {
                    verified = SqliteHistoryGuard.IsRetiredKey(session, receipt.Entry.Scope, receipt.Entry.Key);
                }
            }
            if (verified)
            {
                // 手元と一致する失敗通知で、成功結果の補完待ちまで消しません。
                command.Parameters.AddWithValue("$retainSuccess", receipt.Entry is null && receipt.Outcome != Contracts.Execution.EOutcome.Succeeded);
                command.Parameters.AddWithValue("$success", (int)Contracts.Execution.EOutcome.Succeeded);
                command.CommandText = "DELETE FROM recovery_history WHERE kind=$kind AND key=$id AND NOT($retainSuccess AND json_extract(payload,'$.Outcome')=$success)";
                command.ExecuteNonQuery();
                continue;
            }
            command.CommandText = """
                INSERT INTO recovery_history(kind,key,payload,hash,source_peer) VALUES($kind,$id,$payload,$hash,$peer)
                ON CONFLICT(kind,key) DO NOTHING
                """;
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
            command.Parameters.AddWithValue("$peer", receipt.SourcePeer ?? sourcePeer);
            int inserted = command.ExecuteNonQuery();
            added += inserted;
            if (inserted == 0)
            {
                command.CommandText = "UPDATE recovery_history SET payload=$payload,hash=$hash,source_peer=$peer,problem=NULL WHERE kind=$kind AND key=$id AND hash<>$hash";
                command.ExecuteNonQuery();
            }
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$added", added);
        command.CommandText = """
            UPDATE recovery_progress SET total=CASE WHEN $before=0 THEN $added ELSE total+$added END
            WHERE id=1 AND $added>0
            """;
        command.ExecuteNonQuery();
        RefreshCompatibility(session);
    }

    /// <summary>既存本文を維持した意味上の一致も、同じ元本文の組み合わせに限って再利用します。</summary>
    internal static void RememberVerified(SqliteSession session, RecoveryRecord reference)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.Parameters.AddWithValue("$kind", reference.Kind);
        command.Parameters.AddWithValue("$key", reference.Key);
        command.CommandText = "SELECT hash FROM records WHERE kind=$kind AND key=$key UNION ALL SELECT hash FROM history_index WHERE kind=$kind AND key=$key LIMIT 1";
        if (command.ExecuteScalar() is not string local || string.Equals(local, reference.Hash, StringComparison.Ordinal))
        {
            return;
        }
        command.Parameters.AddWithValue("$remote", reference.Hash);
        command.Parameters.AddWithValue("$local", local);
        command.CommandText = """
            INSERT INTO recovery_verified(kind,key,remote_hash,local_hash) VALUES($kind,$key,$remote,$local)
            ON CONFLICT(kind,key,remote_hash) DO UPDATE SET local_hash=excluded.local_hash WHERE recovery_verified.local_hash<>excluded.local_hash
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>旧同期または直近結果として本文を受信した場合も、同じ未取得情報を完了します。</summary>
    private static void RemovePresent(SqliteSession session)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            DELETE FROM recovery_history WHERE EXISTS(SELECT 1 FROM records
                WHERE records.kind=recovery_history.kind AND records.key=recovery_history.key
                AND records.hash=json_extract(recovery_history.payload,'$.Hash'))
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>背景本文を省略する旧同期へ、未取得のままの保存先を渡しません。</summary>
    internal static void RequireComplete(SqliteSession session)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT 1 FROM recovery_history LIMIT 1";
        if (command.ExecuteScalar() is not null)
        {
            throw new InvalidDataException("未取得履歴があるため従来形式の完全同期はまだ使用できません。");
        }
    }

    /// <summary>未取得がある間は旧版起動を拒否し、すべて確定した後だけ保持対応版へ戻せます。</summary>
    internal static void RefreshCompatibility(SqliteSession session)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            UPDATE repository_identity SET version=CASE WHEN EXISTS(SELECT 1 FROM recovery_history) THEN 4
                WHEN version=4 THEN 3 ELSE version END
            WHERE (version<>4 AND EXISTS(SELECT 1 FROM recovery_history))
                OR (version=4 AND NOT EXISTS(SELECT 1 FROM recovery_history))
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>未確定結果を背景送りせず、操作識別と本文検査値を確認します。</summary>
    internal static void Validate(RecoveryRecord receipt)
    {
        string entryKind = "entry";
        if (receipt.Entry is null && receipt.OperationId == Guid.Empty || receipt.Fingerprint is null || !Enum.IsDefined(receipt.Outcome) || receipt.Outcome == EOutcome.Unknown
            || receipt.Hash is null || receipt.Hash.Length != 64 || !receipt.Hash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("背景取得する履歴の識別情報が不正です。");
        }
        if (receipt.Entry is { } entry && (receipt.OperationId != Guid.Empty || string.IsNullOrWhiteSpace(entry.Scope) || string.IsNullOrWhiteSpace(entry.Key)
            || string.IsNullOrWhiteSpace(entry.Version) || !DurableRepository.IsShared(entry.Scope) || History.HistoryRetentionPolicy.Category(entryKind, entry.Scope) is null))
        {
            throw new InvalidDataException("背景取得する保存項目の用途または版が不正です。");
        }
    }
}
