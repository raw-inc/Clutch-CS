using Microsoft.Data.Sqlite;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

// LEGACY-FORMAT: receipt-fingerprint-v1 — 指紋のない旧履歴は有限単位で補完し、全導入先の補完確認後に旧本文からの補完を撤去します。
/// <summary>共有履歴の正規化指紋を元の保存取引で確定し、照合のたびに本文を復元しません。</summary>
internal static class SqliteReceiptFingerprint
{
    /// <summary>旧writerの更新・削除でも派生索引を失効させ、元本文の寿命より長く保持しません。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            CREATE TABLE IF NOT EXISTS receipt_fingerprints(
                operation TEXT PRIMARY KEY,source_hash TEXT NOT NULL,canonical_hash TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS receipt_fingerprint_deleted AFTER DELETE ON records
            WHEN OLD.kind='receipt' BEGIN DELETE FROM receipt_fingerprints WHERE operation=OLD.key; END;
            CREATE TRIGGER IF NOT EXISTS receipt_fingerprint_changed AFTER UPDATE OF payload,hash ON records
            WHEN OLD.kind='receipt' AND (OLD.hash<>NEW.hash OR OLD.payload<>NEW.payload)
            BEGIN DELETE FROM receipt_fingerprints WHERE operation=OLD.key; END;
            """, connection, transaction);
        command.ExecuteNonQuery();
        EnsureRecoveryColumns(command);
    }

    /// <summary>旧索引を起動時に全件復元せず、背景補完できるNULL列だけを追加します。</summary>
    private static void EnsureRecoveryColumns(SqliteCommand command)
    {
        command.CommandText = "PRAGMA table_info(receipt_fingerprints)";
        HashSet<string> columns = new(StringComparer.Ordinal);
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }
        string fingerprint = "operation_fingerprint", outcome = "outcome", reason = "reason";
        if (!columns.Contains(fingerprint))
        {
            command.CommandText = "ALTER TABLE receipt_fingerprints ADD COLUMN operation_fingerprint TEXT";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains(outcome))
        {
            command.CommandText = "ALTER TABLE receipt_fingerprints ADD COLUMN outcome INTEGER";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains(reason))
        {
            command.CommandText = "ALTER TABLE receipt_fingerprints ADD COLUMN reason TEXT";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>共有範囲だけを保存し、旧版が後から更新した本文に古い指紋を流用しません。</summary>
    internal static void Save(SqliteConnection connection, SqliteTransaction transaction, DurableReceipt receipt, string sourceHash)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        Save(command, receipt, sourceHash);
    }

    /// <summary>既存の有限な履歴レビューへ補完をまとめ、旧本文を同期の読取り中に書き直しません。</summary>
    internal static void Review(SqliteSession session, HistoryRow row)
    {
        string receiptKind = "receipt", shared = "shared";
        if (!string.Equals(row.Kind, receiptKind, StringComparison.Ordinal) || row.Scope is not null && !string.Equals(row.Scope, shared, StringComparison.Ordinal))
        {
            return;
        }
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT 1 FROM receipt_fingerprints WHERE operation=$id AND source_hash=$source AND operation_fingerprint IS NOT NULL AND outcome IS NOT NULL";
        command.Parameters.AddWithValue("$id", row.Key);
        command.Parameters.AddWithValue("$source", row.Hash);
        if (command.ExecuteScalar() is not null)
        {
            return;
        }
        SqlitePayload.Verify(row.Payload, row.Hash);
        DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload) ?? throw new InvalidDataException("共有履歴の本文が不正です。");
        Guid id = Guid.ParseExact(row.Key, "N");
        if (receipt.OperationId != id)
        {
            throw new InvalidDataException("共有履歴の識別が不正です。");
        }
        Save(command, receipt, row.Hash);
    }

    /// <summary>同じ取引の本文だけに紐付け、ローカル履歴を索引へ混ぜません。</summary>
    private static void Save(SqliteCommand command, DurableReceipt receipt, string sourceHash)
    {
        if (!receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope)))
        {
            return;
        }
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO receipt_fingerprints(operation,source_hash,canonical_hash,operation_fingerprint,outcome,reason)
            VALUES($id,$source,$canonical,$fingerprint,$outcome,$reason)
            ON CONFLICT(operation) DO UPDATE SET source_hash=excluded.source_hash,canonical_hash=excluded.canonical_hash,
                operation_fingerprint=excluded.operation_fingerprint,outcome=excluded.outcome,reason=excluded.reason
            """;
        command.Parameters.AddWithValue("$id", receipt.OperationId.ToString("N"));
        command.Parameters.AddWithValue("$source", sourceHash);
        command.Parameters.AddWithValue("$canonical", ContractJson.Hash(receipt));
        command.Parameters.AddWithValue("$fingerprint", receipt.Fingerprint);
        command.Parameters.AddWithValue("$outcome", (int)receipt.Outcome);
        command.Parameters.AddWithValue("$reason", (object?)receipt.Reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
