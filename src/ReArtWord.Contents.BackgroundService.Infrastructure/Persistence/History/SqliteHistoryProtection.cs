using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>現在版が進んだら、以前の復旧基準や未確定記録の保護を再評価します。</summary>
internal static class SqliteHistoryProtection
{
    /// <summary>変更と同じ取引で、依存する過去行だけを再確認対象へ戻します。</summary>
    internal static void Reconsider(SqliteConnection connection, SqliteTransaction transaction, string kind, string key, string? scope)
    {
        string entry = "entry", snapshots = "snapshots", workflows = "workflows", activation = "workflow.activation";
        string operational = "cluster.operational", effective = "cluster.operational.activation", operations = "cluster:operations";
        if (!string.Equals(kind, entry, StringComparison.Ordinal))
        {
            return;
        }
        using SqliteCommand command = new(string.Empty, connection, transaction);
        command.Parameters.AddWithValue("$key", key);
        string? historyScope = null;
        if (string.Equals(scope, workflows, StringComparison.Ordinal) || string.Equals(scope, activation, StringComparison.Ordinal))
        {
            historyScope = "workflow.history:" + JsonSerializer.Deserialize<string[]>(key)![1];
        }
        if (string.Equals(scope, operational, StringComparison.Ordinal) || string.Equals(scope, effective, StringComparison.Ordinal))
        {
            historyScope = "cluster.operational.history";
        }
        if (historyScope is not null)
        {
            command.CommandText = "UPDATE history_candidates SET reviewed_hash=NULL WHERE eligible_after=9223372036854775807 AND kind='entry' AND key IN (SELECT key FROM records WHERE kind='entry' AND scope=$history)";
            command.Parameters.AddWithValue("$history", historyScope);
            command.ExecuteNonQuery();
        }
        if (string.Equals(scope, snapshots, StringComparison.Ordinal))
        {
            command.CommandText = """
                UPDATE history_candidates SET reviewed_hash=NULL WHERE eligible_after=9223372036854775807 AND kind='entry'
                AND key IN (SELECT key FROM records WHERE kind='entry' AND scope='snapshots'
                    AND json_extract(payload,'$.Value.Json.TargetId')=(SELECT json_extract(payload,'$.Value.Json.TargetId') FROM records WHERE kind='entry' AND key=$key))
                """;
            command.ExecuteNonQuery();
        }
        if (string.Equals(scope, operations, StringComparison.Ordinal))
        {
            command.CommandText = "UPDATE history_candidates SET reviewed_hash=NULL WHERE kind='entry' AND key=$member";
            command.Parameters.AddWithValue("$member", DurableRepository.GetAddress("cluster:replication-members", JsonSerializer.Deserialize<string[]>(key)![1]));
            command.ExecuteNonQuery();
        }
    }
}
