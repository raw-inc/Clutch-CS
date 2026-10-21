using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>圧縮ブロック上限を超える確定本文にも、総保持期限を適用します。</summary>
internal static class SqliteHistoryHotDeletion
{
    /// <summary>大きい行は一回に一件だけ検証し、未確定や予約中なら保護を継続します。</summary>
    /// <returns>今回削除した件数です。</returns>
    internal static int DeleteOne(string path, DataRetentionSettings policy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            SELECT r.kind,r.key,r.scope,r.payload,r.hash,d.recorded_at,d.category
            FROM retention_deadlines d JOIN records r ON r.kind=d.kind AND r.key=d.key
            JOIN history_candidates c ON c.kind=r.kind AND c.key=r.key
            WHERE d.delete_at<$now AND d.policy=$policy AND c.eligible_after=9223372036854775807
            ORDER BY d.delete_at LIMIT 1
            """;
        command.Parameters.AddWithValue("$now", HistoryRetentionPolicy.Boundary(policy, now).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$policy", ContractJson.Hash(policy));
        HistoryRow row;
        long recorded;
        ERetentionCategory category;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return 0;
            }
            row = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            recorded = reader.GetInt64(5);
            category = (ERetentionCategory)reader.GetInt32(6);
        }
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$key", row.Key);
        if (RetainedHistoryEligibility.Classify(session, row, recorded, now) is null)
        {
            command.CommandText = "UPDATE retention_deadlines SET delete_at=$later WHERE kind=$kind AND key=$key";
            command.Parameters.AddWithValue("$later", now.AddDays(1).ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            session.Commit();
            return 0;
        }
        SqliteHistoryRetirement.Compressed(session, [row], now);
        int days = Enum.GetValues<ERetentionCategory>().Where(value => !HistoryRetentionPolicy.IsLog(value)).Max(value => policy.Window(value).TotalDays ?? 0);
        DateTimeOffset floor = HistoryRetentionPolicy.Boundary(policy, now).AddDays(-Math.Max(1, days));
        SqliteHistoryRetirement.Retire(session, row, floor);
        RepositoryDocument metadata = new();
        SqliteHistoryGuard.Load(session, metadata);
        RepositoryHistoryGuard.Merge(metadata, new(new Dictionary<ERetentionCategory, DateTimeOffset>(),
            new Dictionary<ERetentionCategory, DateTimeOffset> { [category] = DateTimeOffset.FromUnixTimeSeconds(recorded).AddSeconds(1) }, floor, now));
        session.Save(metadata);
        command.CommandText = """
            DELETE FROM records WHERE kind=$kind AND key=$key;
            DELETE FROM retention_deadlines WHERE kind=$kind AND key=$key;
            DELETE FROM history_candidates WHERE kind=$kind AND key=$key;
            DELETE FROM history_events WHERE operation=$key AND $kind='receipt';
            UPDATE repository_identity SET version=MAX(version,3);
            """;
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return 1;
    }
}
