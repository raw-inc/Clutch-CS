using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>本文ブロックと検索索引を同じSQLite取引で確定する保存構造です。</summary>
internal static class SqliteHistorySchema
{
    /// <summary>通常アクセスではDDLを行わず、保存先初期化時だけ必要な表を確認します。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            CREATE TABLE IF NOT EXISTS history_candidates(
                kind TEXT NOT NULL,key TEXT NOT NULL,recorded_at INTEGER NOT NULL,eligible_after INTEGER NOT NULL,
                value_scopes TEXT NOT NULL,hot_order INTEGER NOT NULL,reviewed_hash TEXT,PRIMARY KEY(kind,key));
            CREATE INDEX IF NOT EXISTS history_candidate_eligible ON history_candidates(recorded_at,hot_order)
                WHERE eligible_after<>9223372036854775807;
            CREATE INDEX IF NOT EXISTS history_candidate_review ON history_candidates(reviewed_hash) WHERE reviewed_hash IS NULL;
            CREATE TABLE IF NOT EXISTS history_blocks(
                id TEXT PRIMARY KEY,data BLOB NOT NULL,raw_length INTEGER NOT NULL,hash TEXT NOT NULL,records INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS history_index(
                kind TEXT NOT NULL,key TEXT NOT NULL,scope TEXT,hash TEXT NOT NULL,
                block_id TEXT NOT NULL,ordinal INTEGER NOT NULL,value_scopes TEXT NOT NULL,recorded_at INTEGER NOT NULL,
                PRIMARY KEY(kind,key));
            CREATE INDEX IF NOT EXISTS history_scope ON history_index(kind,scope,block_id,ordinal);
            CREATE INDEX IF NOT EXISTS history_block_references ON history_index(block_id);
            CREATE TABLE IF NOT EXISTS history_scan(id INTEGER PRIMARY KEY CHECK(id=1),last_rowid INTEGER NOT NULL);
            INSERT INTO history_scan(id,last_rowid) VALUES(1,0) ON CONFLICT(id) DO NOTHING;
            CREATE TABLE IF NOT EXISTS history_classifications(name TEXT PRIMARY KEY);
            INSERT INTO history_classifications(name) VALUES('file-operations-v1') ON CONFLICT(name) DO NOTHING;
            UPDATE history_scan SET last_rowid=0 WHERE id=1 AND changes()<>0;
            INSERT INTO history_classifications(name) VALUES('receipt-fingerprints-v1') ON CONFLICT(name) DO NOTHING;
            UPDATE history_candidates SET reviewed_hash=NULL
            WHERE changes()<>0 AND kind='receipt'
              AND EXISTS(SELECT 1 FROM records r WHERE r.kind=history_candidates.kind AND r.key=history_candidates.key AND r.scope='shared')
              AND NOT EXISTS(SELECT 1 FROM receipt_fingerprints f WHERE f.operation=history_candidates.key AND f.source_hash=history_candidates.reviewed_hash);
            INSERT INTO history_classifications(name) VALUES('receipt-recovery-metadata-v2') ON CONFLICT(name) DO NOTHING;
            UPDATE history_candidates SET reviewed_hash=NULL
            WHERE changes()<>0 AND kind='receipt'
              AND EXISTS(SELECT 1 FROM records r WHERE r.kind=history_candidates.kind AND r.key=history_candidates.key AND r.scope='shared')
              AND NOT EXISTS(SELECT 1 FROM receipt_fingerprints f WHERE f.operation=history_candidates.key AND f.source_hash=history_candidates.reviewed_hash
                  AND f.operation_fingerprint IS NOT NULL AND f.outcome IS NOT NULL);
            """, connection, transaction);
        command.ExecuteNonQuery();
        Recovery.RecoveryRecordLedger.Initialize(connection, transaction);
        LegacyHistoryFormat.Initialize(connection, transaction);
    }
}
