using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>同じSQLite取引の復旧基準だけを再利用し、取引内の書換えで失効させます。</summary>
internal sealed class SqliteHistoryHeads(SqliteConnection connection, SqliteTransaction transaction, Func<string, DurableEntry?> readCurrent)
{
    private long revision = -1;
    private readonly HashSet<string> snapshotKeys = new(StringComparer.Ordinal);
    private bool snapshotsLoaded;
    private readonly Dictionary<string, DurableEntry?> currentEntries = new(StringComparer.Ordinal);

    /// <summary>読取り中の固定状態を再利用し、保存・削除後は同じ取引内でも再取得します。</summary>
    /// <returns>現在の最新取得を指す保存キーかです。</returns>
    internal bool ContainsSnapshot(string address)
    {
        Refresh();
        if (!snapshotsLoaded)
        {
            snapshotKeys.UnionWith(ReadSnapshots());
            snapshotsLoaded = true;
        }
        return snapshotKeys.Contains(address);
    }

    /// <summary>保存版と反映版の本文を、履歴件数分読み直さず同じ固定状態から照合します。</summary>
    /// <returns>現在の定義が参照している履歴版かです。</returns>
    internal bool ContainsRevision(string scope, string key, string expected)
    {
        Refresh();
        string address = DurableRepository.GetAddress(scope, key);
        if (!currentEntries.TryGetValue(address, out DurableEntry? current))
        {
            current = readCurrent(address);
            currentEntries.Add(address, current);
        }
        string property = "Revision";
        return current?.Value is { } value && RetainedHistoryEligibility.HasProperty(value.Json, property, expected);
    }

    /// <summary>同じ取引の更新・削除も反映し、別の取引へ本文を持ち越しません。</summary>
    private void Refresh()
    {
        using SqliteCommand changes = new("SELECT total_changes()", connection, transaction);
        long current = (long)changes.ExecuteScalar()!;
        if (current != revision)
        {
            snapshotKeys.Clear();
            snapshotsLoaded = false;
            currentEntries.Clear();
            revision = current;
        }
    }

    /// <summary>本文への索引でBLOBの逐次書込みを妨げず、対象ごとの同時刻の並び順も維持します。</summary>
    /// <returns>一度の走査で取得した各領域・対象・種類の最新キーです。</returns>
    private HashSet<string> ReadSnapshots()
    {
        using SqliteCommand command = new("""
            SELECT key FROM (
                SELECT key,ROW_NUMBER() OVER (
                    PARTITION BY scope,json_extract(payload,'$.Value.Json.TargetId'),json_extract(payload,'$.Value.Json.Kind')
                    ORDER BY json_extract(payload,'$.Value.Json.CapturedAt') DESC,key DESC) AS position
                FROM records WHERE kind='entry' AND (scope='snapshots' OR scope GLOB '*:detached-snapshots')
            ) WHERE position=1
            """, connection, transaction);
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<string> result = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }
}
