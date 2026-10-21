using Microsoft.Data.Sqlite;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>現在値がない場合だけ圧縮索引を調べ、同じ取引内で一ブロックまで保持して読みます。</summary>
internal sealed class SqliteHistoryReader(SqliteConnection connection, SqliteTransaction transaction, bool enabled)
{
    private LoadedBlock? loaded;

    /// <summary>未保存と破損を区別し、圧縮前と同じ内容を返します。</summary>
    /// <returns>該当する確定履歴、未保存ならnullです。</returns>
    internal HistoryRow? Read(string kind, string key)
    {
        if (!enabled)
        {
            return null;
        }
        using SqliteCommand command = new("SELECT kind,key,scope,hash,block_id,ordinal FROM history_index WHERE kind=$kind AND key=$key", connection, transaction);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Decode(reader)
                             : null;
    }

    /// <summary>ブロック順に列挙して再展開の重複を避け、用途の検索条件を維持します。</summary>
    /// <returns>種類・用途が一致する保存行です。</returns>
    internal IEnumerable<HistoryRow> List(string kind, string? scope = null, IReadOnlySet<string>? valueScopes = null, string? blockId = null, IReadOnlyCollection<string>? keys = null)
    {
        if (!enabled)
        {
            yield break;
        }
        using SqliteCommand command = new("SELECT kind,key,scope,hash,block_id,ordinal FROM history_index WHERE kind=$kind", connection, transaction);
        command.Parameters.AddWithValue("$kind", kind);
        if (blockId is not null)
        {
            command.CommandText += " AND block_id=$block";
            command.Parameters.AddWithValue("$block", blockId);
        }
        if (scope is not null)
        {
            command.CommandText += " AND scope=$scope";
            command.Parameters.AddWithValue("$scope", scope);
        }
        if (valueScopes is not null)
        {
            command.CommandText += " AND EXISTS(SELECT 1 FROM json_each(history_index.value_scopes) WHERE value IN (SELECT value FROM json_each($scopes)))";
            command.Parameters.AddWithValue("$scopes", System.Text.Json.JsonSerializer.Serialize(valueScopes));
        }
        if (keys is not null)
        {
            command.CommandText += " AND key IN (SELECT value FROM json_each($keys))";
            command.Parameters.AddWithValue("$keys", System.Text.Json.JsonSerializer.Serialize(keys));
        }
        command.CommandText += " ORDER BY block_id,ordinal";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return Decode(reader);
        }
    }

    /// <summary>圧縮バイト列と索引を毎回検査し、既知の行は本文を再展開せず観測します。</summary>
    /// <returns>変更された項目だけに本文を持つ観測です。</returns>
    internal IEnumerable<DurableObservation> Observe(string scope, IReadOnlyDictionary<string, string> known, IReadOnlyCollection<string>? keys,
        HistoryObservationCache cache, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            yield break;
        }
        using SqliteCommand command = new("SELECT kind,key,scope,hash,block_id,ordinal FROM history_index WHERE kind='entry' AND scope=$scope", connection, transaction);
        command.Parameters.AddWithValue("$scope", scope);
        if (keys is not null)
        {
            command.CommandText += " AND key IN (SELECT value FROM json_each($keys))";
            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keys));
        }
        command.CommandText += " ORDER BY block_id,ordinal";
        using SqliteDataReader reader = command.ExecuteReader();
        string? blockId = null;
        HistoryObservationCache.Block? block = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string nextId = reader.GetString(4);
            if (!string.Equals(blockId, nextId, StringComparison.Ordinal))
            {
                block = ObserveBlock(nextId, cache);
                blockId = nextId;
            }
            int ordinal = reader.GetInt32(5);
            if (ordinal < 0 || block!.Rows.Count <= ordinal)
            {
                throw new InvalidDataException("履歴ブロックの索引位置が不正です。");
            }
            HistoryObservationCache.Header row = block.Rows[ordinal];
            ValidateIndex(reader, row.Kind, row.Key, row.Scope, row.Hash);
            string key = (JsonSerializer.Deserialize<string[]>(row.Key) ?? throw new InvalidDataException("保存行の識別子が不正です。"))[1];
            DurableEntry? entry = null;
            if (!string.Equals(known.GetValueOrDefault(key), row.Hash, StringComparison.Ordinal))
            {
                entry = JsonSerializer.Deserialize<DurableEntry>(Decode(reader).Payload) ?? throw new InvalidDataException("保存履歴が不正です。");
            }
            yield return new(key, row.Hash, entry);
        }
    }

    /// <summary>圧縮本文のハッシュをストリームで検証し、初見の内容だけ全行を復元します。</summary>
    /// <returns>この取引で内容一致を確認した索引です。</returns>
    private HistoryObservationCache.Block ObserveBlock(string id, HistoryObservationCache cache)
    {
        using SqliteCommand command = new("SELECT rowid,raw_length,hash,length(data) FROM history_blocks WHERE id=$id", connection, transaction);
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader block = command.ExecuteReader();
        if (!block.Read() || block.GetInt32(1) < 1 || HistoryBlockCodec.MaximumBytes < block.GetInt32(1) || HistoryBlockCodec.MaximumBytes < block.GetInt64(3))
        {
            throw new InvalidDataException("検索索引が参照する履歴ブロックがありません。");
        }
        int length = block.GetInt32(1);
        string hash = block.GetString(2);
        using SqliteBlob content = new(connection, "history_blocks", "data", block.GetInt64(0), readOnly: true);
        SqlitePayload.Verify(content, hash);
        HistoryObservationCache.Block? existing = cache.Find(hash, length);
        if (existing is not null)
        {
            return existing;
        }
        return cache.Remember(hash, length, LoadBlock(id).Rows);
    }

    /// <summary>索引と本文の識別が一致しない場合は、未保存と偽らず読取りを失敗させます。</summary>
    /// <returns>検索した元の一行です。</returns>
    private HistoryRow Decode(SqliteDataReader index)
    {
        string id = index.GetString(4);
        HistoryRow[] rows = LoadBlock(id).Rows;
        int ordinal = index.GetInt32(5);
        if (ordinal < 0 || rows.Length <= ordinal)
        {
            throw new InvalidDataException("履歴ブロックの索引位置が不正です。");
        }
        HistoryRow row = rows[ordinal];
        ValidateIndex(index, row.Kind, row.Key, row.Scope, row.Hash);
        return row;
    }

    /// <summary>同じ取引内では直近一ブロックだけ展開済みの本文を再利用します。</summary>
    /// <returns>元の全行を検証した本文です。</returns>
    private LoadedBlock LoadBlock(string id)
    {
        LoadedBlock? current = loaded;
        if (!string.Equals(id, current?.Id, StringComparison.Ordinal))
        {
            using SqliteCommand command = new("SELECT raw_length,hash,length(data),data FROM history_blocks WHERE id=$id", connection, transaction);
            command.Parameters.AddWithValue("$id", id);
            using SqliteDataReader block = command.ExecuteReader();
            if (!block.Read() || block.GetInt32(0) < 1 || HistoryBlockCodec.MaximumBytes < block.GetInt32(0) || HistoryBlockCodec.MaximumBytes < block.GetInt64(2))
            {
                throw new InvalidDataException("検索索引が参照する履歴ブロックがありません。");
            }
            current = new(id, HistoryBlockCodec.Decode((byte[])block.GetValue(3), block.GetInt32(0), block.GetString(1)));
            loaded = current;
        }
        return current!;
    }

    /// <summary>再利用した索引でも現在の検索行との同一性を必ず検査します。</summary>
    private static void ValidateIndex(SqliteDataReader index, string kind, string key, string? rowScope, string hash)
    {
        string? scope = null;
        if (!index.IsDBNull(2))
        {
            scope = index.GetString(2);
        }
        if (!string.Equals(kind, index.GetString(0), StringComparison.Ordinal) || !string.Equals(key, index.GetString(1), StringComparison.Ordinal)
            || !string.Equals(rowScope, scope, StringComparison.Ordinal) || !string.Equals(hash, index.GetString(3), StringComparison.Ordinal))
        {
            throw new InvalidDataException("履歴ブロックと検索索引の内容が一致しません。");
        }
    }

    /// <summary>Reader一個につき一つだけ所有する、検証済みブロックです。</summary>
    private sealed record LoadedBlock(string Id, HistoryRow[] Rows);
}
