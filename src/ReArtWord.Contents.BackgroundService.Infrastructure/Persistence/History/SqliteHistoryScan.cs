using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>旧記録の初回索引化と変更された記録の確認を、少量ずつ進めます。</summary>
internal static class SqliteHistoryScan
{
    /// <summary>元テーブルを何度も先頭から走査せず、新しい行だけを候補へ追加します。</summary>
    internal static void Backfill(SqliteSession session, int maximum)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT last_rowid FROM history_scan WHERE id=1";
        long previous = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT MAX(rowid) FROM (SELECT rowid FROM records WHERE rowid>$after ORDER BY rowid LIMIT $maximum)";
        command.Parameters.AddWithValue("$after", previous);
        command.Parameters.AddWithValue("$maximum", maximum);
        if (command.ExecuteScalar() is not long last)
        {
            return;
        }
        command.Parameters.AddWithValue("$last", last);
        command.CommandText = """
            INSERT INTO history_candidates(kind,key,recorded_at,eligible_after,value_scopes,hot_order,reviewed_hash)
            SELECT kind,key,0,9223372036854775807,'[]',rowid,NULL FROM records
            WHERE rowid>$after AND rowid<=$last
              AND (kind='receipt' OR (kind='entry' AND (substr(scope,1,9)='receipts:' OR scope='peer:persistence-routing'
                OR scope='snapshots' OR substr(scope,1,17)='workflow.history:' OR scope='cluster.operational.history'
                OR scope IN ('cluster:operations','cluster:replication-members','peer:receipt-reconciliation','manual-runs','updates') OR scope LIKE '%:manual-runs'
                OR substr(scope,1,33)='cluster:configuration-operations:' OR scope LIKE '%:workflow-results' OR scope LIKE '%:workflow-result-details' OR scope LIKE '%:detached-snapshots'
                OR scope='file:transactions' OR scope LIKE '%:file-transactions' OR scope LIKE '%:file-recovery' OR scope LIKE '%:file-prepared' OR scope LIKE '%:file-gate-requests')))
            ON CONFLICT(kind,key) DO NOTHING;
            UPDATE history_scan SET last_rowid=$last WHERE id=1;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>本文の検査は保存呼出しから分離し、保護対象も同じ本文なら再解析しません。</summary>
    internal static void Review(SqliteSession session, int maximum, DateTimeOffset now, Contracts.Retention.DataRetentionSettings? retention = null)
    {
        List<(HistoryRow Row, long Recorded)> rows = [];
        using (SqliteCommand query = session.HistoryCommand())
        {
            query.CommandText = """
            SELECT r.kind,r.key,r.scope,r.payload,r.hash,c.recorded_at FROM history_candidates c
            JOIN records r ON r.kind=c.kind AND r.key=c.key WHERE c.reviewed_hash IS NULL LIMIT $maximum
            """;
            query.Parameters.AddWithValue("$maximum", maximum);
            using SqliteDataReader reader = query.ExecuteReader();
            long bytes = 0;
            while (reader.Read())
            {
                string payload = reader.GetString(3);
                if (rows.Count != 0 && 1048576 < bytes + payload.Length * 2L)
                {
                    break;
                }
                rows.Add((new(reader.GetString(0), reader.GetString(1), reader.GetString(2), payload, reader.GetString(4)), reader.GetInt64(5)));
                bytes += payload.Length * 2L;
            }
        }
        using SqliteCommand update = session.HistoryCommand();
        update.CommandText = """
            UPDATE history_candidates SET recorded_at=$recorded,eligible_after=$eligible,value_scopes=$scopes,reviewed_hash=$hash
            WHERE kind=$kind AND key=$key
            """;
        foreach ((HistoryRow row, long recorded) in rows)
        {
            SqliteReceiptFingerprint.Review(session, row);
            LegacyHistoryFormat.Review(session, row);
            HistoryEligibility.Eligibility? eligibility;
            if (retention is null)
            {
                eligibility = HistoryEligibility.Classify(session, row, recorded, now);
            }
            else
            {
                eligibility = RetainedHistoryEligibility.Classify(session, row, recorded, now);
                if (eligibility is not null)
                {
                    SqliteRetentionSchedule.Record(session, row, eligibility.RecordedAt, retention);
                }
            }
            update.Parameters.Clear();
            long firstObserved = recorded;
            if (firstObserved == 0)
            {
                firstObserved = now.ToUnixTimeSeconds();
            }
            update.Parameters.AddWithValue("$recorded", eligibility?.RecordedAt ?? firstObserved);
            update.Parameters.AddWithValue("$eligible", eligibility?.EligibleAfter ?? long.MaxValue);
            update.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(eligibility?.Scopes ?? []));
            update.Parameters.AddWithValue("$hash", row.Hash);
            update.Parameters.AddWithValue("$kind", row.Kind);
            update.Parameters.AddWithValue("$key", row.Key);
            update.ExecuteNonQuery();
        }
    }
}
