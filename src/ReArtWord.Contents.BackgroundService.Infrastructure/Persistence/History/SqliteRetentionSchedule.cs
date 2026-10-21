using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>削除期限を記録単位に固定し、明示されない短縮を既存履歴へ遡及しません。</summary>
internal static class SqliteRetentionSchedule
{
    /// <summary>方針変更の検出と旧アーカイブの移行を有限単位で進めます。</summary>
    internal static void Reconcile(SqliteSession session, DataRetentionSettings settings, int maximum)
    {
        string address = DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, "policy");
        DurableEntry? old = session.Read<DurableEntry>("entry", address);
        NodeValue value = NodeValue.FromObject(settings);
        if (old?.Value is null || !string.Equals(ContractJson.Hash(old.Value), ContractJson.Hash(value), StringComparison.Ordinal))
        {
            using SqliteCommand reset = session.HistoryCommand();
            reset.CommandText = "UPDATE history_candidates SET reviewed_hash=NULL; UPDATE history_scan SET last_rowid=0 WHERE id=1";
            reset.ExecuteNonQuery();
            RepositoryDocument document = new();
            document.Entries[address] = new(RepositoryHistoryGuard.Scope, "policy", ContractJson.Hash(value), value);
            session.Save(document);
        }
        BackfillArchive(session, settings, maximum);
        UpdateDeadlines(session, settings, maximum);
    }

    /// <summary>本文と同じ分類から期限を保存します。</summary>
    internal static void Record(SqliteSession session, HistoryRow row, long recordedAt, DataRetentionSettings settings)
    {
        if (HistoryRetentionPolicy.Category(row.Kind, row.Scope) is { } category)
        {
            Save(session, row.Kind, row.Key, category, recordedAt, settings);
        }
    }

    /// <summary>記録起点からの総保持期限を固定します。</summary>
    private static void Save(SqliteSession session, string kind, string key, ERetentionCategory category, long recordedAt, DataRetentionSettings settings)
    {
        RetentionWindow window = settings.Window(category);
        object deadline = DBNull.Value;
        if (window.TotalDays is int days)
        {
            deadline = DateTimeOffset.FromUnixTimeSeconds(recordedAt).AddDays(days).ToUnixTimeSeconds();
        }
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            INSERT INTO retention_deadlines(kind,key,category,recorded_at,delete_at,policy) VALUES($kind,$key,$category,$recorded,$delete,$policy)
            ON CONFLICT(kind,key) DO UPDATE SET category=excluded.category,recorded_at=excluded.recorded_at,policy=excluded.policy,
              delete_at=CASE WHEN $existing=1 THEN excluded.delete_at
                WHEN retention_deadlines.delete_at IS NULL OR excluded.delete_at IS NULL THEN NULL
                ELSE MAX(retention_deadlines.delete_at,excluded.delete_at) END
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$category", (int)category);
        command.Parameters.AddWithValue("$recorded", recordedAt);
        command.Parameters.AddWithValue("$delete", deadline);
        command.Parameters.AddWithValue("$policy", ContractJson.Hash(settings));
        command.Parameters.AddWithValue("$existing", settings.ApplyToExisting);
        command.ExecuteNonQuery();
    }

    /// <summary>過去の圧縮済み行を展開せず、新しい期限索引へ移します。</summary>
    private static void BackfillArchive(SqliteSession session, DataRetentionSettings settings, int maximum)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT last_rowid FROM retention_archive_scan WHERE id=1";
        long previous = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT rowid,kind,key,scope,recorded_at FROM history_index WHERE rowid>$previous ORDER BY rowid LIMIT $maximum";
        command.Parameters.AddWithValue("$previous", previous);
        command.Parameters.AddWithValue("$maximum", maximum);
        List<(long Row, string Kind, string Key, string Scope, long Recorded)> rows = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
            }
        }
        foreach ((long Row, string Kind, string Key, string Scope, long Recorded) row in rows)
        {
            if (HistoryRetentionPolicy.Category(row.Kind, row.Scope) is { } category)
            {
                Save(session, row.Kind, row.Key, category, row.Recorded, settings);
            }
        }
        if (rows.Count != 0)
        {
            command.CommandText = "UPDATE retention_archive_scan SET last_rowid=$last WHERE id=1";
            command.Parameters.AddWithValue("$last", rows[^1].Row);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>延長・明示短縮を一回に有限件だけ処理します。</summary>
    private static void UpdateDeadlines(SqliteSession session, DataRetentionSettings settings, int maximum)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT kind,key,category,recorded_at FROM retention_deadlines WHERE policy<$policy UNION ALL SELECT kind,key,category,recorded_at FROM retention_deadlines WHERE $policy<policy LIMIT $maximum";
        command.Parameters.AddWithValue("$policy", ContractJson.Hash(settings));
        command.Parameters.AddWithValue("$maximum", maximum);
        List<(string Kind, string Key, ERetentionCategory Category, long Recorded)> rows = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), (ERetentionCategory)reader.GetInt32(2), reader.GetInt64(3)));
            }
        }
        foreach ((string Kind, string Key, ERetentionCategory Category, long Recorded) row in rows)
        {
            Save(session, row.Kind, row.Key, row.Category, row.Recorded, settings);
        }
    }

    /// <summary>圧縮は現在方針、削除は固定期限をそれぞれ適用します。</summary>
    /// <returns>種類番号から圧縮境界への小さいJSONです。</returns>
    internal static string RawCutoffs(DataRetentionSettings settings, DateTimeOffset now)
        => JsonSerializer.Serialize(Enum.GetValues<ERetentionCategory>().ToDictionary(category => (int)category,
            category => HistoryRetentionPolicy.Boundary(settings, now).AddDays(-settings.Window(category).RawDays).ToUnixTimeSeconds()));
}
