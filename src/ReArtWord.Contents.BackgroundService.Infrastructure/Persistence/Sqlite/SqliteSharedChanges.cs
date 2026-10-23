using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>ローカル診断の更新と共有本文の変更を、同じSQLite取引の世代で区別します。</summary>
internal static class SqliteSharedChanges
{
    /// <summary>旧版のSQL書込みにも追従する変更世代を、起動時だけ準備します。</summary>
    // 抑制理由: 展開する式は共有索引と同じコンパイル時SQL定数であり、外部入力を連結しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "固定SQL定数のscope修飾だけを展開し、外部値のepochはパラメーターで渡します。")]
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (SqliteCommand column = new("SELECT 1 FROM pragma_table_info('records') WHERE name='shared_revision'", connection, transaction))
        {
            if (column.ExecuteScalar() is null)
            {
                column.CommandText = "ALTER TABLE records ADD COLUMN shared_revision INTEGER NOT NULL DEFAULT 0; DROP INDEX IF EXISTS shared_content_lookup; "
                    + "CREATE INDEX shared_content_lookup ON records(kind,key,hash,scope,shared_revision) WHERE " + SqliteSharedFingerprint.ScopeFilter;
                column.ExecuteNonQuery();
            }
        }
        string old = SqliteSharedFingerprint.ScopeFilter.Replace("scope", "OLD.scope", StringComparison.Ordinal);
        string current = SqliteSharedFingerprint.ScopeFilter.Replace("scope", "NEW.scope", StringComparison.Ordinal);
        using SqliteCommand command = new($$"""
            CREATE TABLE IF NOT EXISTS shared_changes(id INTEGER PRIMARY KEY CHECK(id=1),epoch TEXT NOT NULL,revision INTEGER NOT NULL);
            INSERT INTO shared_changes(id,epoch,revision) VALUES(1,$epoch,0) ON CONFLICT(id) DO NOTHING;
            CREATE TRIGGER IF NOT EXISTS shared_record_insert BEFORE INSERT ON records
            WHEN (NEW.kind IN ('entry','conflict') AND {{current}})
                OR EXISTS(SELECT 1 FROM records WHERE kind=NEW.kind AND key=NEW.key AND kind IN ('entry','conflict') AND {{SqliteSharedFingerprint.ScopeFilter}})
            BEGIN UPDATE shared_changes SET revision=revision+1 WHERE id=1; END;
            CREATE TRIGGER IF NOT EXISTS shared_row_insert AFTER INSERT ON records
            WHEN NEW.kind IN ('entry','conflict') AND {{current}}
            BEGIN
                UPDATE records SET shared_revision=(SELECT revision FROM shared_changes WHERE id=1) WHERE rowid=NEW.rowid;
            END;
            DROP TRIGGER IF EXISTS shared_record_update;
            CREATE TRIGGER shared_record_update AFTER UPDATE OF kind,key,scope,payload,hash,rowid,oid,_rowid_ ON records
            WHEN (OLD.kind IN ('entry','conflict') AND {{old}}) OR (NEW.kind IN ('entry','conflict') AND {{current}})
            BEGIN
                UPDATE shared_changes SET revision=revision+1 WHERE id=1;
                UPDATE records SET shared_revision=(SELECT revision FROM shared_changes WHERE id=1) WHERE rowid=NEW.rowid;
            END;
            CREATE TRIGGER IF NOT EXISTS shared_record_delete AFTER DELETE ON records
            WHEN OLD.kind IN ('entry','conflict') AND {{old}}
            BEGIN UPDATE shared_changes SET revision=revision+1 WHERE id=1; END;
            """, connection, transaction);
        command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("N"));
        command.ExecuteNonQuery();
    }

    /// <summary>本文と同じ読取りスナップショットの世代を取得します。</summary>
    /// <returns>DB再作成も区別する世代です。</returns>
    internal static Generation Read(SqliteSession session)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT epoch,revision FROM shared_changes WHERE id=1";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || !Guid.TryParse(reader.GetString(0), out Guid epoch) || epoch == Guid.Empty || reader.GetInt64(1) < 0)
        {
            throw new InvalidDataException("共有変更世代が不正です。");
        }
        return new(epoch, reader.GetInt64(1));
    }

    /// <summary>待機中の再試行判定では、現在値の全体ハッシュを再計算しません。</summary>
    /// <returns>再作成と共有変更を区別する安価な世代です。</returns>
    internal static string Stamp(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        Generation value = Read(session);
        return value.Epoch.ToString("N") + ":" + value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>プロセス間・媒体再作成を含めて照合する変更位置です。</summary>
    internal sealed record Generation(Guid Epoch, long Revision);
}
