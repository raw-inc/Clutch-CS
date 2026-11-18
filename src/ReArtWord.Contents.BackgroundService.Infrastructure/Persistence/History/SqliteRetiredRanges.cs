using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>退役済みIDを索引で照合し、隣接区間の合成も本文削除と同じ取引で確定します。</summary>
internal static class SqliteRetiredRanges
{
    /// <summary>旧保存先への読取りではDDLを行わず、通常の初期化時にだけ区間表を用意します。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = new("""
            CREATE TABLE IF NOT EXISTS retired_id_ranges(
                bucket TEXT NOT NULL, first_id INTEGER NOT NULL, last_id INTEGER NOT NULL,
                shared INTEGER NOT NULL CHECK(shared IN (0,1)), payload TEXT NOT NULL, hash TEXT NOT NULL,
                PRIMARY KEY(bucket,first_id), CHECK(0<=first_id AND first_id<=last_id AND last_id<=4294967295));
            CREATE INDEX IF NOT EXISTS retired_id_ranges_shared ON retired_id_ranges(bucket,first_id) WHERE shared=1;
            """, connection, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>確認できていない欠番を飛び越さず、重複・隣接する区間だけを書き換えます。</summary>
    internal static void Add(SqliteSession session, RetiredIdRange range)
    {
        range.Validate();
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT payload,hash FROM retired_id_ranges WHERE bucket=$bucket AND first_id<=$last AND last_id>=$first ORDER BY first_id";
        command.Parameters.AddWithValue("$bucket", range.Bucket);
        command.Parameters.AddWithValue("$first", (long)range.First - 1);
        command.Parameters.AddWithValue("$last", (long)range.Last + 1);
        List<RetiredIdRange> found = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                RetiredIdRange existing = Read(reader);
                if (!string.Equals(existing.Bucket, range.Bucket, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("退役済みIDの保存先が一致しません。");
                }
                found.Add(existing);
            }
        }
        foreach (RetiredIdRange existing in found)
        {
            range = range with { First = Math.Min(range.First, existing.First), Last = Math.Max(range.Last, existing.Last) };
        }
        if (found.Count == 1 && found[0] == range)
        {
            return;
        }
        command.Parameters["$first"].Value = range.First;
        command.Parameters["$last"].Value = range.Last;
        command.CommandText = "DELETE FROM retired_id_ranges WHERE bucket=$bucket AND first_id<=$last AND last_id>=$first";
        command.ExecuteNonQuery();
        string payload = JsonSerializer.Serialize(range);
        command.CommandText = "INSERT INTO retired_id_ranges(bucket,first_id,last_id,shared,payload,hash) VALUES($bucket,$first,$last,$shared,$payload,$hash)";
        command.Parameters.AddWithValue("$shared", range.Shared);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
        command.ExecuteNonQuery();

        // 旧版は区間を読めないため、同じ保存先を開いて退役情報を読み落とすことを防ぎます。
        // 背景取得中の4からは6へ移し、未取得履歴があるという既存の保護も維持します。
        command.CommandText = "UPDATE repository_identity SET version=CASE WHEN version=4 THEN 6 ELSE 5 END WHERE version<5";
        command.ExecuteNonQuery();
    }

    /// <summary>指定した一点を索引で確認し、区間全体を通常の受付へ読み込みません。</summary>
    /// <returns>退役を確認できた元の区間、なければnullです。</returns>
    internal static RetiredIdRange? Find(SqliteCommand command, RetiredIdRange point)
    {
        command.CommandText = "SELECT payload,hash FROM retired_id_ranges WHERE bucket=$bucket AND first_id<=$id AND last_id>=$id ORDER BY first_id DESC LIMIT 1";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$bucket", point.Bucket);
        command.Parameters.AddWithValue("$id", point.First);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        RetiredIdRange found = Read(reader);
        if (!string.Equals(found.Bucket, point.Bucket, StringComparison.Ordinal) || point.First < found.First || found.Last < point.First)
        {
            throw new InvalidDataException("退役済みIDの索引と本文が一致しません。");
        }
        return found;
    }

    /// <summary>共有用途だけを書き出し、Peer固有の受付を他Peerへ渡しません。</summary>
    /// <returns>正規化済みの実退役区間です。</returns>
    internal static RetiredIdRange[] Export(SqliteSession session, bool sharedOnly = true)
    {
        using SqliteCommand command = session.HistoryCommand();
        command.CommandText = "SELECT payload,hash FROM retired_id_ranges";
        if (sharedOnly)
        {
            command.CommandText += " WHERE shared=1";
        }
        command.CommandText += " ORDER BY bucket,first_id";
        List<RetiredIdRange> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }
        return rows.ToArray();
    }

    /// <summary>一括取込みだけが、既存の区間証拠も含めて予約・過去本文の復活を判定します。</summary>
    internal static void Load(SqliteSession session, RepositoryDocument document)
    {
        foreach (RetiredIdRange range in Export(session, sharedOnly: false))
        {
            ((document.History ??= new()).RetiredRanges ??= new()).Add(range);
        }
    }

    /// <summary>区間の破損や不正な用途を、未退役という判断へ置き換えません。</summary>
    /// <returns>内容を検証した区間です。</returns>
    private static RetiredIdRange Read(SqliteDataReader reader)
    {
        string payload = reader.GetString(0);
        SqlitePayload.Verify(payload, reader.GetString(1));
        RetiredIdRange range = JsonSerializer.Deserialize<RetiredIdRange>(payload) ?? throw new InvalidDataException("退役済みIDの区間がありません。");
        range.Validate();
        return range;
    }
}
