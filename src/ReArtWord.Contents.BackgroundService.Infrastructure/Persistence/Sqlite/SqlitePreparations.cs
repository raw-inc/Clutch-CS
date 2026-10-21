using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>現在の準備だけを索引で読み、再参加確認を過去履歴の大きさから切り離します。</summary>
internal static class SqlitePreparations
{
    /// <summary>本文の増分書込みを妨げず、未確認判断だけを同じ取引の小さい索引表へ反映します。</summary>
    // 抑制理由: 展開するのは同じアセンブリ内の固定プロトコル名とenum値だけで、外部入力を連結しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "固定した索引述語だけを共用し、外部入力は含みません。")]
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new($$"""
            CREATE TABLE IF NOT EXISTS preparation_versions(operation TEXT PRIMARY KEY,source_hash TEXT NOT NULL) WITHOUT ROWID;
            CREATE TRIGGER IF NOT EXISTS preparation_version_insert AFTER INSERT ON records WHEN NEW.kind='prepared'
            BEGIN
                INSERT INTO preparation_versions(operation,source_hash) VALUES(NEW.key,NEW.hash)
                ON CONFLICT(operation) DO UPDATE SET source_hash=excluded.source_hash WHERE source_hash<>excluded.source_hash;
            END;
            CREATE TRIGGER IF NOT EXISTS preparation_version_update AFTER UPDATE ON records WHEN OLD.kind='prepared' OR NEW.kind='prepared'
            BEGIN
                DELETE FROM preparation_versions WHERE OLD.kind='prepared' AND operation=OLD.key AND (NEW.kind<>'prepared' OR NEW.key<>OLD.key);
                INSERT INTO preparation_versions(operation,source_hash) SELECT NEW.key,NEW.hash WHERE NEW.kind='prepared'
                ON CONFLICT(operation) DO UPDATE SET source_hash=excluded.source_hash WHERE source_hash<>excluded.source_hash;
            END;
            CREATE TRIGGER IF NOT EXISTS preparation_version_delete AFTER DELETE ON records WHEN OLD.kind='prepared'
            BEGIN DELETE FROM preparation_versions WHERE operation=OLD.key; END;
            INSERT INTO preparation_versions(operation,source_hash) SELECT key,hash FROM records WHERE kind='prepared'
            ON CONFLICT(operation) DO UPDATE SET source_hash=excluded.source_hash WHERE source_hash<>excluded.source_hash;
            DELETE FROM preparation_versions WHERE NOT EXISTS(SELECT 1 FROM records WHERE kind='prepared' AND key=operation);
            CREATE TABLE IF NOT EXISTS replication_recovery(operation TEXT PRIMARY KEY,address TEXT NOT NULL,source_hash TEXT NOT NULL) WITHOUT ROWID;
            CREATE TRIGGER IF NOT EXISTS replication_recovery_insert AFTER INSERT ON records
            WHEN {{PendingCondition("NEW.")}}
            BEGIN
                INSERT INTO replication_recovery(operation,address,source_hash) VALUES(json_extract(NEW.key,'$[1]'),NEW.key,NEW.hash)
                ON CONFLICT(operation) DO UPDATE SET address=excluded.address,source_hash=excluded.source_hash;
            END;
            CREATE TRIGGER IF NOT EXISTS replication_recovery_update AFTER UPDATE ON records
            WHEN (OLD.kind='entry' AND OLD.scope='cluster:operations') OR (NEW.kind='entry' AND NEW.scope='cluster:operations')
            BEGIN
                DELETE FROM replication_recovery WHERE OLD.kind='entry' AND OLD.scope='cluster:operations' AND operation=json_extract(OLD.key,'$[1]');
                INSERT INTO replication_recovery(operation,address,source_hash)
                SELECT json_extract(NEW.key,'$[1]'),NEW.key,NEW.hash WHERE {{PendingCondition("NEW.")}}
                ON CONFLICT(operation) DO UPDATE SET address=excluded.address,source_hash=excluded.source_hash;
            END;
            CREATE TRIGGER IF NOT EXISTS replication_recovery_delete AFTER DELETE ON records
            WHEN OLD.kind='entry' AND OLD.scope='cluster:operations'
            BEGIN
                DELETE FROM replication_recovery WHERE operation=json_extract(OLD.key,'$[1]');
            END;
            INSERT INTO history_classifications(name) VALUES('replication-recovery-v1') ON CONFLICT(name) DO NOTHING;
            INSERT INTO replication_recovery(operation,address,source_hash)
            SELECT json_extract(key,'$[1]'),key,hash FROM records WHERE changes()<>0 AND {{PendingCondition(string.Empty)}}
            ON CONFLICT(operation) DO UPDATE SET address=excluded.address,source_hash=excluded.source_hash;
            """, connection, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>同じ新形式の判断だけを索引表へ投影します。</summary>
    /// <returns>固定された行参照と保存契約から作るSQL述語です。</returns>
    private static string PendingCondition(string prefix) => $$"""
        {{prefix}}kind='entry' AND {{prefix}}scope='cluster:operations' AND json_extract({{prefix}}payload,'$.Value.Json.Outcome')={{(int)EOutcome.Unknown}}
        AND json_extract({{prefix}}payload,'$.Value.Json.Reason') IN ('{{ReplicationJournal.Preparing}}','{{ReplicationJournal.CommitDecided}}','{{ReplicationJournal.AbortDecided}}')
        """;

    /// <summary>現在の準備と全参加者の確認待ちを、一つの有限な巡回へまとめます。</summary>
    /// <returns>前回位置より後の確認対象です。</returns>
    internal static IReadOnlyList<Guid> ReadRecovery(string path, Guid after, int maximum, SqlitePreparationCache cache, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 64);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        IEnumerable<Guid> prepared = cache.SharedIds(session, cancellationToken).Where(id => 0 < id.CompareTo(after)).Take(maximum);
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = """
            SELECT i.operation FROM replication_recovery i
            JOIN records r ON r.kind='entry' AND r.key=i.address AND r.hash=i.source_hash
            WHERE i.operation>$after ORDER BY i.operation LIMIT $maximum
            """;
        command.Parameters.AddWithValue("$after", after.ToString("N"));
        command.Parameters.AddWithValue("$maximum", maximum);
        using SqliteDataReader reader = command.ExecuteReader();
        List<Guid> result = [.. prepared];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(OperationId(reader.GetString(0)));
        }
        return result.Distinct().Order().Take(maximum).ToArray();
    }

    // LEGACY-FORMAT: preparation-scope-v1 — 旧readerとの混在中は分類も保存します。未分類の旧本文は初回検証で分類し、確認結果だけを短時間再利用します。
    /// <summary>ローカルだけの準備を、クラスタ再参加の保留へ混入しません。</summary>
    /// <returns>準備対象に共有範囲が含まれるかの分類です。</returns>
    internal static string Scope(PreparedBatch batch)
    {
        if (batch.Mutations.Any(mutation => DurableRepository.IsShared(mutation.Scope)))
        {
            return "shared";
        }
        return "local";
    }

    /// <summary>確定結果や履歴を復元せず、有限の準備識別子だけを返します。</summary>
    /// <returns>同じ読取り取引で確認した元操作です。</returns>
    internal static IReadOnlyList<Guid> Read(string path, Guid after, int maximum, SqlitePreparationCache cache, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 64);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return cache.SharedIds(session, cancellationToken).Where(id => 0 < id.CompareTo(after)).Take(maximum).ToArray();
    }

    /// <summary>不正な保存位置を、Hostへ漏れる書式例外ではなく保存検査の失敗として扱います。</summary>
    /// <returns>検査済みの元操作です。</returns>
    internal static Guid OperationId(string value)
    {
        string format = "N";
        if (!Guid.TryParseExact(value, format, out Guid id) || id == Guid.Empty)
        {
            throw new InvalidDataException("復旧対象の操作識別が不正です。");
        }
        return id;
    }
}
