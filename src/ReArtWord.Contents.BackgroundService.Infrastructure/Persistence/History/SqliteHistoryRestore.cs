using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

// LEGACY-FORMAT: history-raw-v1 — 旧版復帰用の全展開は、旧履歴readerのサポート終了時に撤去します。
/// <summary>明示した停止中保守で元本文を通常行へ戻し、完了した場合だけ旧保存形式へ戻します。</summary>
internal static class SqliteHistoryRestore
{
    /// <summary>再び現在版になった復旧基準だけを通常行へ戻し、圧縮位置の差による再同期を防ぎます。</summary>
    internal static void PromoteProtected(SqliteSession session, RepositorySnapshot snapshot, CancellationToken cancellationToken)
    {
        HashSet<string> protectedVersions = (snapshot.ProtectedHistory ?? []).ToHashSet(StringComparer.Ordinal);
        string[] keys = snapshot.Entries.Where(entry => protectedVersions.Contains(DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version))
            .Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key)).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
        {
            return;
        }
        using SqliteCommand command = session.HistoryCommand();
        SqliteHistoryReader history = new(command.Connection!, command.Transaction!, true);
        foreach (HistoryRow row in history.List("entry", keys: keys).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurableEntry entry = JsonSerializer.Deserialize<DurableEntry>(row.Payload) ?? throw new InvalidDataException("現在版の履歴本文が不正です。");
            string entryKind = "entry";
            if (entry.Value is null || HistoryRetentionPolicy.Category(entryKind, entry.Scope) is not { } category || !RetainedHistoryEligibility.Protected(session, entry, category))
            {
                continue;
            }
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$key", row.Key);
            command.Parameters.AddWithValue("$scope", row.Scope!);
            command.Parameters.AddWithValue("$payload", row.Payload);
            command.Parameters.AddWithValue("$hash", row.Hash);
            command.CommandText = """
                INSERT INTO records(kind,key,scope,payload,hash) VALUES('entry',$key,$scope,$payload,$hash)
                ON CONFLICT(kind,key) DO NOTHING;
                DELETE FROM history_index WHERE kind='entry' AND key=$key
                    AND EXISTS(SELECT 1 FROM records WHERE kind='entry' AND key=$key AND hash=$hash);
                """;
            command.ExecuteNonQuery();
            SqliteHistoryCandidates.Enqueue(command.Connection!, command.Transaction!, row.Kind, row.Key, row.Scope, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>対応済みDBだけを少量ずつ縮め、既存DBを稼働中に全再構築しません。</summary>
    /// <returns>次回にも回収できる空きページが残るかです。</returns>
    internal static bool Reclaim(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        try
        {
            command.CommandText = "PRAGMA auto_vacuum";
            if ((long)command.ExecuteScalar()! != 2)
            {
                return false;
            }

            // 読取り後の書込み昇格を待ち続けず、通常処理と競合する回収は次回へ延期します。
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            command.Transaction = transaction;
            command.CommandText = "PRAGMA incremental_vacuum(64)";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            command.CommandText = "PRAGMA freelist_count";
            bool pending = 0 < (long)command.ExecuteScalar()!;
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return pending;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    /// <summary>一つのブロックの展開・索引除去を一取引にし、途中停止から続行可能にします。</summary>
    /// <returns>展開した件数、全完了なら0です。</returns>
    internal static int ExpandOne(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT block_id FROM history_index ORDER BY block_id LIMIT 1";
        if (command.ExecuteScalar() is not string id)
        {
            command.CommandText = "DELETE FROM history_blocks; UPDATE repository_identity SET version=CASE WHEN version>=3 THEN version ELSE 1 END; UPDATE history_scan SET last_rowid=0 WHERE id=1;";
            command.ExecuteNonQuery();
            session.Commit();
            return 0;
        }
        HistoryRow[] rows = session.ArchivedBlock(id);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$rows", JsonSerializer.Serialize(rows));
        command.CommandText = """
            SELECT COUNT(*) FROM records r JOIN json_each($rows) item
            ON r.kind=json_extract(item.value,'$.Kind') AND r.key=json_extract(item.value,'$.Key')
            WHERE r.hash<>json_extract(item.value,'$.Hash')
            """;
        if ((long)command.ExecuteScalar()! != 0 || rows.Length == 0)
        {
            throw new InvalidDataException("通常行と圧縮索引が矛盾しているため展開を中止しました。");
        }
        command.CommandText = """
            INSERT INTO records(kind,key,scope,payload,hash)
            SELECT json_extract(value,'$.Kind'),json_extract(value,'$.Key'),json_extract(value,'$.Scope'),json_extract(value,'$.Payload'),json_extract(value,'$.Hash')
            FROM json_each($rows) WHERE true ON CONFLICT(kind,key) DO NOTHING;
            DELETE FROM history_index WHERE block_id=$id;
            DELETE FROM history_blocks WHERE id=$id;
            """;
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
        session.Commit();
        return rows.Length;
    }

    /// <summary>停止中にだけSQLite全体を再構築し、以後は少量ずつ空きページを回収できるようにします。</summary>
    internal static void Compact(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; VACUUM;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA integrity_check";
        string expected = "ok";
        if (!string.Equals(command.ExecuteScalar() as string, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("縮小後のSQLite整合性を確認できません。");
        }
        command.CommandText = "UPDATE history_scan SET last_rowid=0 WHERE id=1";
        command.ExecuteNonQuery();
    }
}
