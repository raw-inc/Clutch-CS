using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>期限、再実行防止、確定イベントと基準点を保存本文と同じ取引へ置きます。</summary>
internal static class SqliteRetentionSchema
{
    /// <summary>追加表は起動時だけ作成し、本文削除までは旧保存形式を維持します。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            CREATE TABLE IF NOT EXISTS retention_deadlines(
                kind TEXT NOT NULL,key TEXT NOT NULL,category INTEGER NOT NULL,recorded_at INTEGER NOT NULL,delete_at INTEGER,policy TEXT NOT NULL,
                PRIMARY KEY(kind,key));
            CREATE INDEX IF NOT EXISTS retention_deadline_policy ON retention_deadlines(policy);
            CREATE INDEX IF NOT EXISTS retention_deadline_due ON retention_deadlines(delete_at) WHERE delete_at IS NOT NULL;
            CREATE INDEX IF NOT EXISTS retention_deadline_policy_due ON retention_deadlines(policy,delete_at,kind,key) WHERE delete_at IS NOT NULL;
            CREATE TABLE IF NOT EXISTS retention_archive_scan(id INTEGER PRIMARY KEY CHECK(id=1),last_rowid INTEGER NOT NULL);
            INSERT INTO retention_archive_scan(id,last_rowid) VALUES(1,0) ON CONFLICT(id) DO NOTHING;
            CREATE TABLE IF NOT EXISTS retired_operations(
                id TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,shared INTEGER NOT NULL,outcome INTEGER NOT NULL,reason TEXT);
            CREATE TABLE IF NOT EXISTS retired_keys(scope TEXT NOT NULL,key TEXT NOT NULL,PRIMARY KEY(scope,key));
            CREATE TABLE IF NOT EXISTS retention_changes(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL);
            INSERT INTO retention_changes(id,revision) VALUES(1,0) ON CONFLICT(id) DO NOTHING;
            CREATE TRIGGER IF NOT EXISTS retired_key_insert AFTER INSERT ON retired_keys BEGIN UPDATE retention_changes SET revision=revision+1 WHERE id=1; END;
            CREATE TRIGGER IF NOT EXISTS retired_key_delete AFTER DELETE ON retired_keys BEGIN UPDATE retention_changes SET revision=revision+1 WHERE id=1; END;
            CREATE TABLE IF NOT EXISTS history_events(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,operation TEXT NOT NULL UNIQUE,recorded_at INTEGER NOT NULL,causal TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS history_events_time ON history_events(recorded_at,sequence);
            CREATE TABLE IF NOT EXISTS history_checkpoints(
                id TEXT PRIMARY KEY,sequence INTEGER NOT NULL,created_at INTEGER NOT NULL,
                payload BLOB NOT NULL,raw_length INTEGER NOT NULL,hash TEXT NOT NULL,compressed INTEGER NOT NULL,delete_at INTEGER);
            CREATE INDEX IF NOT EXISTS history_checkpoint_sequence ON history_checkpoints(sequence);
            CREATE INDEX IF NOT EXISTS history_checkpoint_retention ON history_checkpoints(delete_at,created_at,id);
            CREATE INDEX IF NOT EXISTS history_checkpoint_raw ON history_checkpoints(created_at) WHERE compressed=0;
            """, connection, transaction);
        command.ExecuteNonQuery();
    }
}
