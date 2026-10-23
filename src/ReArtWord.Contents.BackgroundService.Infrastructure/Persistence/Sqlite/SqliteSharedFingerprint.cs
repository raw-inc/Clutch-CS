using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>共有行の実バイト列を検証し、JSONオブジェクトを再生成せず変更を識別します。</summary>
internal static class SqliteSharedFingerprint
{
    /// <summary>ローカルデータを含めない読取りと索引で共用する条件です。</summary>
    // 抑制理由: 部分索引と読取りが一致する固定SQLで、外部入力や実行時変更を受け入れません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW410", Justification = "索引と読取りで共用するコンパイル時SQL定数です。")]
    internal const string ScopeFilter = "substr(scope,1,5) <> 'peer:' AND substr(scope,1,9) <> 'receipts:' AND substr(scope,1,8) <> 'cluster:'";

    /// <summary>ローカル行を走査せず、行の整合性と変更有無を同じ取引内で確認します。</summary>
    /// <returns>検証した行の識別子と内容ハッシュ。UTF-16保存では通常の読取り検証へ戻すnullです。</returns>
    internal static VerifiedRows? Read(SqliteConnection connection, SqliteTransaction transaction, SqliteVersionRows? previous = null)
    {
        using SqliteCommand command = new("PRAGMA encoding", connection, transaction);
        string utf8 = "UTF-8";
        if (!string.Equals(command.ExecuteScalar() as string, utf8, StringComparison.Ordinal))
        {
            return null;
        }
        command.CommandText = "SELECT rowid,hash,kind,key,scope,shared_revision FROM records INDEXED BY shared_content_lookup WHERE kind IN ('entry','conflict') AND " + ScopeFilter + " ORDER BY kind,key";
        using SqliteDataReader reader = command.ExecuteReader();
        using IncrementalHash fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        List<Row> rows = [];
        Span<byte> digest = stackalloc byte[32];
        while (reader.Read())
        {
            long id = reader.GetInt64(0), revision = reader.GetInt64(5);
            string hash = reader.GetString(1), kind = reader.GetString(2), key = reader.GetString(3), scope = reader.GetString(4);
            Row? cached = previous?.FindVerified(id);
            if (cached is not null && cached.Revision == revision && string.Equals(cached.Hash, hash, StringComparison.Ordinal)
                && string.Equals(cached.Kind, kind, StringComparison.Ordinal) && string.Equals(cached.Key, key, StringComparison.Ordinal)
                && string.Equals(cached.Scope, scope, StringComparison.Ordinal))
            {
                fingerprint.AppendData(Convert.FromHexString(hash));
                rows.Add(cached);
                continue;
            }
            using SqliteBlob content = new(connection, "records", "payload", id, readOnly: true);
            _ = SHA256.HashData(content, digest);
            if (!string.Equals(Convert.ToHexString(digest), hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存行の内容が破損しています。");
            }
            fingerprint.AppendData(digest);
            rows.Add(new(id, kind, key, scope, hash, content.Length, revision));
        }
        _ = fingerprint.GetHashAndReset(digest);
        return new(Convert.ToHexString(digest), rows);
    }

    /// <summary>同じ読取り取引で実バイト列を検証した共有行です。</summary>
    internal sealed record Row(long Id, string Kind, string Key, string Scope, string Hash, long Length, long Revision);

    /// <summary>集合の変更識別と、行単位の再利用判定を同じ検証結果へ結び付けます。</summary>
    internal sealed record VerifiedRows(string Fingerprint, IReadOnlyList<Row> Rows);
}
