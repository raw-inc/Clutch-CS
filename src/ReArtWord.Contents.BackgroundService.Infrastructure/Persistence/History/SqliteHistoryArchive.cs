using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>短い選別・確定取引の間でのみ保存を変更し、圧縮計算は呼出側の排他外へ渡します。</summary>
internal static class SqliteHistoryArchive
{
    /// <summary>期限または件数超過で、直近保護を終えた確定結果から一ブロックを選びます。</summary>
    /// <returns>固定した元本文です。圧縮中に変わった行は確定時に除外します。</returns>
    internal static HistoryRow[] Prepare(string path, HistoryArchiveOptions options, DateTimeOffset now, CancellationToken cancellationToken = default)
        => PrepareCore(path, options, now, null, cancellationToken);

    /// <summary>種類別の圧縮期間を確定履歴へ適用します。</summary>
    /// <returns>圧縮前の有限な固定本文です。</returns>
    internal static HistoryRow[] PrepareRetained(string path, HistoryArchiveOptions options, DataRetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken = default)
        => PrepareCore(path, options, now, retention, cancellationToken);

    /// <summary>旧圧縮と共通保持で同じ保存・選別・整合検査を使用します。</summary>
    /// <returns>固定した候補です。</returns>
    private static HistoryRow[] PrepareCore(string path, HistoryArchiveOptions options, DateTimeOffset now, DataRetentionSettings? retention, CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled)
        {
            return [];
        }
        using SqliteSession session = new(path);
        if (retention is not null)
        {
            SqliteRetentionSchedule.Reconcile(session, retention, options.BatchRecords);
        }
        SqliteHistoryScan.Backfill(session, options.BatchRecords);
        LegacyHistoryFormat.Backfill(session, options.BatchRecords, cancellationToken);
        SqliteHistoryScan.Review(session, options.BatchRecords, now, retention);
        using SqliteCommand query = session.HistoryCommand();
        query.CommandText = "DELETE FROM history_blocks WHERE NOT EXISTS(SELECT 1 FROM history_index WHERE history_index.block_id=history_blocks.id)";
        query.ExecuteNonQuery();
        query.CommandText = "SELECT COUNT(*) FROM history_candidates WHERE eligible_after<>9223372036854775807";
        long hot = (long)query.ExecuteScalar()!;
        query.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        long cutoff = now.AddDays(-options.KeepDays).ToUnixTimeSeconds();
        long candidateCutoff = cutoff;
        if (retention is not null)
        {
            int shortest = Enum.GetValues<Contracts.Retention.ERetentionCategory>().Where(category => !HistoryRetentionPolicy.IsLog(category))
                .Min(category => retention.Window(category).RawDays);
            candidateCutoff = Math.Max(cutoff, HistoryRetentionPolicy.Boundary(retention, now).AddDays(-shortest).ToUnixTimeSeconds());
        }
        if (options.MaximumHotRecords < hot)
        {
            candidateCutoff = long.MaxValue;
        }
        query.Parameters.AddWithValue("$cutoff", cutoff);
        query.Parameters.AddWithValue("$candidateCutoff", candidateCutoff);
        query.Parameters.AddWithValue("$excess", Math.Max(0, hot - options.MaximumHotRecords));
        query.Parameters.AddWithValue("$maximum", options.BatchRecords);
        string raw = "{}";
        if (retention is not null)
        {
            raw = SqliteRetentionSchedule.RawCutoffs(retention, now);
        }
        query.Parameters.AddWithValue("$raw", raw);
        List<HistoryRow> rows = [];
        List<HistoryRow> oversized = [];
        if (retention is not null)
        {
            // 移行対象を通常の期限条件へORで混ぜず、少数の旧形式索引から本文を取得します。
            query.CommandText = """
                SELECT r.kind,r.key,r.scope,r.payload,r.hash FROM history_legacy_formats legacy
                JOIN history_candidates c ON c.kind=legacy.kind AND c.key=legacy.key AND c.reviewed_hash=legacy.source_hash
                JOIN records r ON r.kind=legacy.kind AND r.key=legacy.key AND r.hash=legacy.source_hash
                WHERE c.eligible_after<=$now
                  AND NOT EXISTS(SELECT 1 FROM reservations WHERE address=r.key OR (r.kind='receipt' AND operation=r.key))
                  AND NOT EXISTS(SELECT 1 FROM records p WHERE p.kind='prepared' AND r.kind='receipt' AND p.key=r.key)
                ORDER BY c.recorded_at,c.hot_order LIMIT $maximum
                """;
            ReadCandidates(query, rows, oversized, cancellationToken);
        }
        if (rows.Count < options.BatchRecords)
        {
            query.CommandText = """
                SELECT r.kind,r.key,r.scope,r.payload,r.hash FROM history_candidates c
                JOIN records r ON r.kind=c.kind AND r.key=c.key AND r.hash=c.reviewed_hash
                LEFT JOIN retention_deadlines d ON d.kind=c.kind AND d.key=c.key
                WHERE c.eligible_after<>9223372036854775807
                  AND c.recorded_at<=$candidateCutoff
                  AND c.eligible_after<=$now AND (c.recorded_at<=COALESCE(json_extract($raw,'$."'||d.category||'"'),$cutoff) OR $excess>0)
                  AND NOT EXISTS(SELECT 1 FROM reservations WHERE address=r.key OR (r.kind='receipt' AND operation=r.key))
                  AND NOT EXISTS(SELECT 1 FROM records p WHERE p.kind='prepared' AND r.kind='receipt' AND p.key=r.key)
                ORDER BY c.recorded_at,c.hot_order LIMIT $maximum
                """;
            query.Parameters["$maximum"].Value = options.BatchRecords - rows.Count;
            ReadCandidates(query, rows, oversized, cancellationToken);
        }
        foreach (HistoryRow row in oversized)
        {
            query.Parameters.Clear();
            query.CommandText = "UPDATE history_candidates SET eligible_after=9223372036854775807 WHERE kind=$kind AND key=$key AND reviewed_hash=$hash";
            query.Parameters.AddWithValue("$kind", row.Kind);
            query.Parameters.AddWithValue("$key", row.Key);
            query.Parameters.AddWithValue("$hash", row.Hash);
            query.ExecuteNonQuery();
        }
        session.Commit();
        return rows.ToArray();
    }

    /// <summary>両方の索引を使っても同じ本文を重複せず、合計容量を一ブロックに制限します。</summary>
    private static void ReadCandidates(SqliteCommand query, List<HistoryRow> rows, List<HistoryRow> oversized, CancellationToken cancellationToken)
    {
        long bytes = 2;
        HashSet<(string Kind, string Key)> selected = [];
        foreach (HistoryRow row in rows)
        {
            bytes += HistoryBlockCodec.EstimatedBytes(row);
            selected.Add((row.Kind, row.Key));
        }
        using (SqliteDataReader reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                HistoryRow row = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                if (!selected.Add((row.Kind, row.Key)))
                {
                    continue;
                }
                long size = HistoryBlockCodec.EstimatedBytes(row);
                if (HistoryBlockCodec.MaximumBytes < size + 2)
                {
                    oversized.Add(row);
                    continue;
                }
                if (HistoryBlockCodec.MaximumBytes < bytes + size)
                {
                    break;
                }
                SqlitePayload.Verify(row.Payload, row.Hash);
                rows.Add(row);
                bytes += size;
            }
        }
    }

    /// <summary>検証済みブロック・索引・通常行の除去を一取引にし、旧読取実装には形式不一致として停止させます。</summary>
    /// <returns>圧縮中も変わらず、移動を確定できた件数です。</returns>
    internal static int Commit(string path, IReadOnlyList<HistoryRow> rows, byte[] data, int length, string hash,
        DateTimeOffset now, CancellationToken cancellationToken = default)
        => CommitCore(path, rows, data, length, hash, now, false, cancellationToken);

    /// <summary>祖先と保持境界を先に記録してから共有履歴も圧縮へ移します。</summary>
    /// <returns>確認済みの移動件数です。</returns>
    internal static int CommitRetained(string path, IReadOnlyList<HistoryRow> rows, byte[] data, int length, string hash,
        DateTimeOffset now, CancellationToken cancellationToken = default)
        => CommitCore(path, rows, data, length, hash, now, true, cancellationToken);

    /// <summary>圧縮計算中の変更を再照合し、同じ取引で索引と元行を切り替えます。</summary>
    /// <returns>実際に確定した件数です。</returns>
    private static int CommitCore(string path, IReadOnlyList<HistoryRow> rows, byte[] data, int length, string hash,
        DateTimeOffset now, bool retained, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HistoryRow[] verified = HistoryBlockCodec.Decode(data, length, hash);
        if (!verified.SequenceEqual(rows))
        {
            throw new InvalidDataException("圧縮確認後の履歴内容が元の選別と一致しません。");
        }
        using SqliteSession session = new(path);
        string id = Guid.NewGuid().ToString("N");
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "INSERT INTO history_blocks(id,data,raw_length,hash,records) VALUES($id,$data,$length,$hash,$count)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$data", data);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$count", rows.Count);
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO history_index(kind,key,scope,hash,block_id,ordinal,value_scopes,recorded_at)
            SELECT r.kind,r.key,r.scope,r.hash,$id,CAST(json_extract(item.value,'$.Ordinal') AS INTEGER),c.value_scopes,c.recorded_at
            FROM json_each($rows) item
            JOIN records r ON r.kind=json_extract(item.value,'$.Kind') AND r.key=json_extract(item.value,'$.Key') AND r.hash=json_extract(item.value,'$.Hash')
            JOIN history_candidates c ON c.kind=r.kind AND c.key=r.key AND c.reviewed_hash=r.hash
            WHERE c.eligible_after<=$now
              AND NOT EXISTS(SELECT 1 FROM reservations WHERE address=r.key OR (r.kind='receipt' AND operation=r.key))
              AND NOT EXISTS(SELECT 1 FROM records p WHERE p.kind='prepared' AND r.kind='receipt' AND p.key=r.key)
            ON CONFLICT(kind,key) DO NOTHING
            """;
        command.Parameters.AddWithValue("$rows", JsonSerializer.Serialize(rows.Select((row, ordinal) => new { row.Kind, row.Key, row.Hash, Ordinal = ordinal })));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        int moved = command.ExecuteNonQuery();
        if (moved == 0)
        {
            return 0;
        }
        if (retained)
        {
            command.CommandText = "SELECT kind,key FROM history_index WHERE block_id=$id";
            HashSet<(string Kind, string Key)> actual = [];
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    actual.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            SqliteHistoryRetirement.Compressed(session, rows.Where(row => actual.Contains((row.Kind, row.Key))).ToArray(), now);
            command.CommandText = "UPDATE repository_identity SET version=MAX(version,3)";
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        command.CommandText = """
            DELETE FROM records WHERE (kind,key) IN (SELECT kind,key FROM history_index WHERE block_id=$id);
            DELETE FROM history_candidates WHERE (kind,key) IN (SELECT kind,key FROM history_index WHERE block_id=$id);
            UPDATE repository_identity SET version=MAX(version,2);
            """;
        command.ExecuteNonQuery();
        session.Commit();
        return moved;
    }
}
