using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>既存TEXT行へ小さな再利用バッファーで書き、全JSONのDOM・文字列・バイト配列を作りません。</summary>
internal static class SqliteJsonSnapshot
{
    /// <summary>incremental I/OはDBの保存encodingのバイト列なので、UTF-16の既存DBを直接上書きしません。</summary>
    /// <returns>UTF-8 JSONを直接扱える保存形式ならtrueです。</returns>
    internal static bool SupportsStreaming(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA encoding";
        return string.Equals(command.ExecuteScalar() as string, "UTF-8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>元バイト列のハッシュを検査し、JSON文書や全体文字列を介さず型付き履歴を復元します。</summary>
    /// <returns>現在行の値、未保存ならnullです。</returns>
    internal static T? Read<T>(SqliteConnection connection, SqliteTransaction transaction, string scope, string key, CancellationToken cancellationToken) where T : class
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT rowid,hash FROM records WHERE kind='entry' AND key=$key";
        command.Parameters.AddWithValue("$key", DurableRepository.GetAddress(scope, key));
        long row;
        string expected;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }
            row = reader.GetInt64(0);
            expected = reader.GetString(1);
        }
        using SqliteBlob blob = new(connection, "records", "payload", row, readOnly: true);
        using SHA256 hash = SHA256.Create();
        using CryptoStream input = new(blob, hash, CryptoStreamMode.Read, leaveOpen: true);
        SnapshotEntry<T> entry = JsonSerializer.Deserialize<SnapshotEntry<T>>(input, ContractJson.Options) ?? throw new InvalidDataException("局所診断snapshotが欠損しています。");
        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("局所診断snapshotの末尾が不正です。");
        }
        string actual = Convert.ToHexString(hash.Hash!);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存行の内容が破損しています。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Value is null)
        {
            return null;
        }
        if (entry.Value.Kind != EValueKind.Json || entry.Value.Json is null)
        {
            throw new InvalidDataException("局所診断snapshotのJSON値が欠損しています。");
        }
        return entry.Value.Json;
    }

    /// <summary>長さを先に数え、同じ取引内のTEXT領域へ直接書いて旧readerとの互換を維持します。</summary>
    internal static void Write(SqliteConnection connection, SqliteTransaction transaction, string scope, string key, Action<Utf8JsonWriter> writeValue, CancellationToken cancellationToken)
    {
        string version = Guid.NewGuid().ToString("N");
        using SnapshotBuffer buffer = new(cancellationToken);
        Serialize(buffer, scope, key, version, writeValue);
        long length = buffer.Length;
        string hash = buffer.FinishHash();
        ConfigurePageCache(connection, transaction);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO records(kind,key,scope,payload,hash) VALUES('_snap',$key,$scope,CAST(zeroblob($length) AS TEXT),$hash) RETURNING rowid";
        command.Parameters.AddWithValue("$key", DurableRepository.GetAddress(scope, key));
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$hash", hash);
        long row = (long)command.ExecuteScalar()!;
        using (SqliteBlob blob = new(connection, "records", "payload", row))
        {
            buffer.Restart(blob);
            Serialize(buffer, scope, key, version, writeValue);
            if (buffer.Length != length || !string.Equals(buffer.FinishHash(), hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存中に診断snapshotが変更されました。");
            }
        }

        // 新しい行が揃ってから置換し、旧ペイロード全体のundo 操作履歴を作るin-place上書きを避けます。
        command.CommandText = "DELETE FROM records WHERE kind='entry' AND key=$key; UPDATE records SET kind='entry' WHERE rowid=$row";
        command.Parameters.AddWithValue("$row", row);
        command.ExecuteNonQuery();
    }

    /// <summary>DurableEntryとNodeValueの既存envelopeに、所有中の値だけを直接埋め込みます。</summary>
    private static void Serialize(SnapshotBuffer buffer, string scope, string key, string version, Action<Utf8JsonWriter> writeValue)
    {
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString(nameof(DurableEntry.Scope), scope);
        writer.WriteString(nameof(DurableEntry.Key), key);
        writer.WriteString(nameof(DurableEntry.Version), version);
        writer.WriteStartObject(nameof(DurableEntry.Value));
        writer.WriteNumber(nameof(NodeValue.Kind), (int)EValueKind.Json);
        writer.WritePropertyName(nameof(NodeValue.Json));
        writeValue(writer);
        writer.WriteString(nameof(NodeValue.Binary), string.Empty);
        writer.WriteEndObject();
        writer.WriteNull(nameof(DurableEntry.ParentVersion));
        writer.WriteNull(nameof(DurableEntry.MergedVersions));
        writer.WriteEndObject();
    }

    /// <summary>外側の保存契約を維持し、Jsonだけを用途の型へ直接復元します。</summary>
    private sealed record SnapshotEntry<T>(string Scope, string Key, string Version, SnapshotValue<T>? Value, string? ParentVersion = null, IReadOnlyList<string>? MergedVersions = null) where T : class;

    /// <summary>NodeValueと同じ保存形で中間JsonElementだけを省きます。</summary>
    private sealed record SnapshotValue<T>(EValueKind Kind, T? Json, ReadOnlyMemory<byte> Binary) where T : class;

    /// <summary>書込み取引中だけ有限のpage キャッシュを使い、TEXT予約領域の途中spillを抑えます。</summary>
    private static void ConfigurePageCache(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA cache_size=-65536";
        command.ExecuteNonQuery();
    }

    /// <summary>確定した片を直ちにハッシュとSQLiteへ渡し、大きな入力トークン用領域はpoolへ保持しません。</summary>
    private sealed class SnapshotBuffer(CancellationToken cancellationToken) : IBufferWriter<byte>, IDisposable
    {
        private static readonly ArrayPool<byte> pool = ArrayPool<byte>.Create(65536, 2);
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // 抑制理由: 借用領域の拡張時と終了時に旧配列を返却します。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "再利用bufferの所有領域を交換します。")]
        private byte[] buffer = pool.Rent(65536);
        private Stream? output;

        /// <summary>現在の一巡で確定したバイト数です。</summary>
        internal long Length
        {
            get; private set;
        }

        /// <summary>同じバッファーで書込みの一巡へ移ります。</summary>
        internal void Restart(Stream destination)
        {
            output = destination;
            Length = 0;
        }

        /// <summary>既存TEXT ペイロードと同じSHA-256表記です。</summary>
        /// <returns>一巡のハッシュです。</returns>
        internal string FinishHash()
        {
            Span<byte> digest = stackalloc byte[32];
            _ = hash.GetHashAndReset(digest);
            return Convert.ToHexString(digest);
        }

        /// <summary>確定したバイトだけを書き、取消を各片で確認します。</summary>
        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> bytes = buffer.AsSpan(0, count);
            output?.Write(bytes);
            hash.AppendData(bytes);
            Length = checked(Length + count);
        }

        /// <summary>次のJSON片の領域を返します。</summary>
        /// <returns>再利用する作業領域です。</returns>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return buffer;
        }

        /// <summary>次のJSON片の領域を返します。</summary>
        /// <returns>再利用する作業領域です。</returns>
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return buffer;
        }

        /// <summary>最大二個の通常バッファーだけをpoolへ返し、内容を消去します。</summary>
        public void Dispose()
        {
            if (buffer.Length != 0)
            {
                pool.Return(buffer, clearArray: true);
                buffer = [];
            }
            hash.Dispose();
        }

        /// <summary>単一トークンが通常領域を超える場合だけ拡張します。</summary>
        private void Ensure(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (buffer.Length < sizeHint)
            {
                byte[] next = pool.Rent(sizeHint);
                pool.Return(buffer, clearArray: true);
                buffer = next;
            }
        }
    }
}
