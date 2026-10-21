using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>保存と同じ取引で小さい未確認マーカーだけを書き、本文の分類は背景処理へ渡します。</summary>
internal static class SqliteHistoryCandidates
{
    /// <summary>履歴を参照できる用途では、現在行だけを扱う診断用の直接I/Oを使用しません。</summary>
    /// <returns>本文の分類対象になり得る用途かです。</returns>
    internal static bool IsHistoryScope(string? scope) => scope?.StartsWith("receipts:", StringComparison.Ordinal) == true
        || HistoryRetentionPolicy.Category("entry", scope) is not null;

    /// <summary>通常設定・業務現在値の保存へ、履歴の分類や圧縮を混入させません。</summary>
    internal static void Enqueue(SqliteConnection connection, SqliteTransaction transaction, string kind, string key, string? scope, DateTimeOffset now)
    {
        SqliteHistoryProtection.Reconsider(connection, transaction, kind, key, scope);
        string receipt = "receipt", entry = "entry", routing = "peer:persistence-routing";
        bool eligibleKind = string.Equals(kind, receipt, StringComparison.Ordinal)
            || string.Equals(kind, entry, StringComparison.Ordinal) && IsHistoryScope(scope);
        if (!eligibleKind)
        {
            return;
        }
        using SqliteCommand command = new("""
            INSERT INTO history_candidates(kind,key,recorded_at,eligible_after,value_scopes,hot_order,reviewed_hash)
            SELECT kind,key,$now,9223372036854775807,'[]',rowid,NULL FROM records WHERE kind=$kind AND key=$key
            ON CONFLICT(kind,key) DO UPDATE SET recorded_at=excluded.recorded_at,eligible_after=excluded.eligible_after,reviewed_hash=NULL;
            """, connection, transaction);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
        if (string.Equals(kind, receipt, StringComparison.Ordinal))
        {
            string ownerKey = DurableRepository.GetAddress(routing, "owner:" + key);
            command.CommandText = """
                INSERT INTO history_candidates(kind,key,recorded_at,eligible_after,value_scopes,hot_order,reviewed_hash)
                SELECT kind,key,$now,9223372036854775807,'[]',rowid,NULL FROM records WHERE kind='entry' AND key=$owner
                ON CONFLICT(kind,key) DO UPDATE SET eligible_after=excluded.eligible_after,reviewed_hash=NULL;
                """;
            command.Parameters.AddWithValue("$owner", ownerKey);
            command.ExecuteNonQuery();
        }
    }
}
