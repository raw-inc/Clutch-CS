using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>件数または時間の先着で、履歴本文を含まない復旧基準点を取得します。</summary>
internal static class SqliteHistoryCheckpoints
{
    /// <summary>初回基準点を一つ作り、以後は変更がある時だけ条件を評価します。</summary>
    /// <returns>今回新しい基準点を保存したかです。</returns>
    internal static bool Capture(string path, DataRetentionSettings policy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        DurableEntry? requested = session.Read<DurableEntry>("entry", DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, RepositoryCheckpointRequest.Key));
        bool required = requested?.Value?.Json.ValueKind == System.Text.Json.JsonValueKind.True;
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='history_events'),0)";
        long sequence = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT sequence,created_at FROM history_checkpoints ORDER BY sequence DESC,created_at DESC LIMIT 1";
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                long previous = reader.GetInt64(0);
                DateTimeOffset created = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1));
                if (!required && (sequence <= previous || sequence - previous < policy.CheckpointEvents && now < created.AddDays(policy.CheckpointDays)))
                {
                    return false;
                }
            }
        }
        DurableEntry[] current = session.List<DurableEntry>("entry", sharedOnly: true, cancellationToken: cancellationToken).Concat(session.List<DurableEntry>("conflict", sharedOnly: true, cancellationToken: cancellationToken))
            .Where(entry => HistoryRetentionPolicy.Category("entry", entry.Scope) is null).Distinct().ToArray();
        if (current.Length == 0)
        {
            return false;
        }
        RepositoryDocument metadata = new();
        SqliteCausality.Load(session, metadata, current.Select(entry => (entry.Scope, entry.Key)));
        SqliteHistoryGuard.Load(session, metadata);
        RepositorySnapshot snapshot = new(current, [], CausalEntries: RepositoryCausality.Export(metadata, current), LegacyEntries: RepositoryLegacyAncestry.Export(metadata, current),
            HistoryBoundary: RepositoryHistoryGuard.Read(metadata), RetiredOperations: SqliteHistoryGuard.Export(session));
        byte[] raw = HistoryCheckpointCodec.Encode(snapshot);
        command.CommandText = "INSERT INTO history_checkpoints(id,sequence,created_at,payload,raw_length,hash,compressed,delete_at) VALUES($id,$sequence,$time,$data,$length,$hash,0,$delete)";
        command.Parameters.AddWithValue("$id", Guid.CreateVersion7(now).ToString("N"));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$time", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$data", raw);
        command.Parameters.AddWithValue("$length", raw.Length);
        command.Parameters.AddWithValue("$hash", HistoryCheckpointCodec.Hash(raw));
        object deleteAt = DBNull.Value;
        if (policy.Window(ERetentionCategory.Snapshots).TotalDays is int days)
        {
            deleteAt = now.AddDays(days).ToUnixTimeSeconds();
        }
        command.Parameters.AddWithValue("$delete", deleteAt);
        command.ExecuteNonQuery();
        if (required)
        {
            RepositoryDocument completed = new();
            RepositoryCheckpointRequest.Set(completed, false);
            session.Save(completed);
        }
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return true;
    }

    /// <summary>基準点自身にも保持設定を適用し、最新と一つ前の復旧基準は残します。</summary>
    internal static void Maintain(string path, DataRetentionSettings policy, DateTimeOffset now)
    {
        using SqliteSession session = new(path);
        using SqliteCommand command = session.HistoryCommand();
        RetentionWindow retention = policy.Window(ERetentionCategory.Snapshots);
        DateTimeOffset boundary = HistoryRetentionPolicy.Boundary(policy, now);

        // 本文の大きい行は候補だけを読み、期限変更のない周期では被覆索引内で確認を完了します。
        command.CommandText = """
            UPDATE history_checkpoints SET delete_at=CASE WHEN $days IS NULL THEN NULL
              WHEN $existing=1 THEN created_at+$days*86400 WHEN delete_at IS NULL THEN NULL
              ELSE MAX(delete_at,created_at+$days*86400) END
            WHERE id IN (SELECT id FROM history_checkpoints INDEXED BY history_checkpoint_retention
              WHERE ($days IS NULL AND delete_at IS NOT NULL)
              OR ($days IS NOT NULL AND $existing=1 AND (delete_at IS NULL OR delete_at<>created_at+$days*86400))
              OR ($days IS NOT NULL AND $existing=0 AND delete_at<created_at+$days*86400));
            """;
        object totalDays = DBNull.Value;
        if (retention.TotalDays is int finiteDays)
        {
            totalDays = finiteDays;
        }
        command.Parameters.AddWithValue("$days", totalDays);
        command.Parameters.AddWithValue("$existing", policy.ApplyToExisting);
        command.ExecuteNonQuery();
        if (retention.TotalDays is not null)
        {
            command.CommandText = "DELETE FROM history_checkpoints WHERE delete_at<$before AND id NOT IN (SELECT id FROM history_checkpoints ORDER BY sequence DESC,created_at DESC LIMIT 2)";
            command.Parameters.AddWithValue("$before", boundary.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT id,payload,raw_length,hash FROM history_checkpoints WHERE compressed=0 AND created_at<$raw ORDER BY created_at LIMIT 1";
        command.Parameters.AddWithValue("$raw", boundary.AddDays(-retention.RawDays).ToUnixTimeSeconds());
        string? id = null;
        byte[]? compressed = null;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                id = reader.GetString(0);
                byte[] data = (byte[])reader.GetValue(1);
                _ = HistoryCheckpointCodec.Decode(data, reader.GetInt32(2), reader.GetString(3), false);
                compressed = HistoryCheckpointCodec.Compress(data);
            }
        }
        if (id is not null)
        {
            command.CommandText = "UPDATE history_checkpoints SET payload=$data,compressed=1 WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$data", compressed!);
            command.ExecuteNonQuery();
        }
        session.Commit();
    }

    /// <summary>大きい本文を展開せず、基準点の区間と容量を列挙します。</summary>
    /// <returns>新しい順の有限な基準点一覧です。</returns>
    internal static HistoryCheckpointInfo[] List(string path)
    {
        using SqliteSession session = new(path, write: false);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT id,sequence,created_at,compressed,length(payload),raw_length FROM history_checkpoints ORDER BY sequence DESC,created_at DESC LIMIT 100";
        List<HistoryCheckpointInfo> result = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.GetInt64(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)), reader.GetInt32(3) != 0, reader.GetInt64(4), reader.GetInt64(5)));
        }
        return result.ToArray();
    }
}
