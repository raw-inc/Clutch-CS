using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>変更監視で本文の整合性を維持し、未変更本文のオブジェクト復元を省きます。</summary>
internal static class SqliteObservations
{
    /// <summary>一度の読取り取引を所有し、変更監視で書込みを行いません。</summary>
    /// <returns>本文を検証した観測です。</returns>
    internal static IReadOnlyList<DurableObservation> Read(string path, string scope, IReadOnlyDictionary<string, string> known,
        IReadOnlySet<string>? keys, HistoryObservationCache cache, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(known);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys is { Count: 0 })
        {
            return [];
        }
        using SqliteSession session = new(path, write: false);
        return session.Observe(scope, known, keys, cache, cancellationToken);
    }

    /// <summary>通常行と圧縮行を一つの読取り取引で照合します。</summary>
    /// <returns>同じ指紋なら本文を省略した、現在の対象集合です。</returns>
    internal static IReadOnlyList<DurableObservation> Read(SqliteConnection connection, SqliteTransaction transaction, SqliteHistoryReader history,
        string scope, IReadOnlyDictionary<string, string> known, IReadOnlySet<string>? keys, HistoryObservationCache cache, CancellationToken cancellationToken)
    {
        List<DurableObservation> result = [];
        using SqliteCommand encoding = new("PRAGMA encoding", connection, transaction);
        bool utf8 = string.Equals(encoding.ExecuteScalar() as string, "UTF-8", StringComparison.Ordinal);
        string[]? addresses = keys?.Select(key => DurableRepository.GetAddress(scope, key)).ToArray();
        using SqliteCommand command = new("SELECT json_extract(key,'$[1]'),rowid,hash FROM records WHERE kind='entry' AND scope=$scope", connection, transaction);
        command.Parameters.AddWithValue("$scope", scope);
        if (addresses is not null)
        {
            // 指定キーから主キーを検索し、他の大きな本文を持つ行の走査を避けます。
            command.CommandText = "SELECT json_extract(records.key,'$[1]'),records.rowid,records.hash FROM json_each($keys) selected CROSS JOIN records WHERE records.kind='entry' AND records.key=selected.value AND records.scope=$scope";
            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(addresses));
        }
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = reader.GetString(0);
                string hash = reader.GetString(2);
                long rowId = reader.GetInt64(1);
                bool changed = !string.Equals(known.GetValueOrDefault(key), hash, StringComparison.Ordinal);
                DurableEntry? entry = null;
                if (!changed && utf8)
                {
                    using SqliteBlob content = new(connection, "records", "payload", rowId, readOnly: true);
                    SqlitePayload.Verify(content, hash);
                }
                if (changed || !utf8)
                {
                    using SqliteCommand read = new("SELECT payload FROM records WHERE rowid=$id", connection, transaction);
                    read.Parameters.AddWithValue("$id", rowId);
                    string payload = (string?)read.ExecuteScalar() ?? throw new InvalidDataException("保存行がありません。");
                    SqlitePayload.Verify(payload, hash);
                    if (changed)
                    {
                        entry = JsonSerializer.Deserialize<DurableEntry>(payload) ?? throw new InvalidDataException("保存行が不正です。");
                    }
                }
                Add(result, scope, key, hash, entry);
            }
        }
        foreach (DurableObservation observed in history.Observe(scope, known, addresses, cache, cancellationToken))
        {
            Add(result, scope, observed.Key, observed.Fingerprint, observed.Entry);
        }
        return result;
    }

    /// <summary>索引と本文の用途・識別子が一致しない場合は候補として採用しません。</summary>
    private static void Add(List<DurableObservation> result, string scope, string key, string hash, DurableEntry? entry)
    {
        if (entry is not null && (!string.Equals(entry.Scope, scope, StringComparison.Ordinal) || !string.Equals(entry.Key, key, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("保存行の識別情報が一致しません。");
        }
        result.Add(new(key, hash, entry));
    }
}
