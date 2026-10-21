using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>共有履歴の本文を一件ずつ検証し、既知本文の照合には有限の指紋キャッシュを使います。</summary>
internal static class SqliteReceiptExport
{
    /// <summary>本文参照の利用終了まで同じ読取り取引を保持し、例外時も必ず解放します。</summary>
    /// <returns>固定した版を使用する処理結果です。</returns>
    internal static async Task<T> ReadAsync<T>(string path, ConcurrentDictionary<string, string> hashes, Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        Recovery.RecoveryRecordLedger.RequireComplete(session);
        SnapshotExport export = new(SqliteSnapshot.Export(session, includeReceipts: false), session.ExportReceipts(hashes, cancellationToken),
            id => session.Read<DurableReceipt>("receipt", id.ToString("N"), remember: false));
        return await read(export).ConfigureAwait(false);
    }

    /// <summary>行の場所と内容識別だけを列挙し、旧writerの未分類行は本文でも確認します。</summary>
    /// <returns>同じ取引が生きている間だけ有効な履歴参照です。</returns>
    internal static IReadOnlyList<SnapshotReceipt> Read(SqliteConnection connection, SqliteTransaction transaction,
        ConcurrentDictionary<string, string> hashes, CancellationToken cancellationToken)
    {
        if (32768 < hashes.Count)
        {
            hashes.Clear();
        }
        bool streaming = SqliteJsonSnapshot.SupportsStreaming(connection, transaction);
        using SqliteCommand command = new("""
            SELECT r.rowid,r.key,r.hash,r.scope,f.canonical_hash FROM records r
            LEFT JOIN receipt_fingerprints f ON f.operation=r.key AND f.source_hash=r.hash
            WHERE r.kind='receipt' AND (r.scope='shared' OR r.scope IS NULL)
            """, connection, transaction);
        using SqliteDataReader reader = command.ExecuteReader();
        List<SnapshotReceipt> receipts = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long row = reader.GetInt64(0);
            Guid id = Guid.ParseExact(reader.GetString(1), "N");
            string expected = reader.GetString(2);
            bool classified = !reader.IsDBNull(3);
            string? retainedHash = reader.IsDBNull(4) ? null
                                                      : reader.GetString(4);
            if (retainedHash is not null && (retainedHash.Length != 64 || !retainedHash.All(char.IsAsciiHexDigit)))
            {
                throw new InvalidDataException("共有履歴の照合指紋が不正です。");
            }
            DurableReceipt ReadValue() => ReadOne(connection, transaction, row, expected, id, streaming, classified, cancellationToken);
            if (!classified && !ReadValue().Entries.All(entry => DurableRepository.IsShared(entry.Scope)))
            {
                continue;
            }
            string HashValue(DurableReceipt? value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retainedHash is not null)
                {
                    return retainedHash;
                }
                if (hashes.TryGetValue(expected, out string? known))
                {
                    return known;
                }
                string calculated = ContractJson.Hash(value ?? ReadValue());
                if (hashes.Count < 32768)
                {
                    hashes.TryAdd(expected, calculated);
                }
                return calculated;
            }
            receipts.Add(new(id, ReadValue, HashValue));
        }
        return receipts;
    }

    /// <summary>UTF-8のTEXT行は巨大な中間文字列を作らず読み、元のハッシュと操作識別を検証します。</summary>
    /// <returns>確認した一件の共有履歴です。</returns>
    private static DurableReceipt ReadOne(SqliteConnection connection, SqliteTransaction transaction, long row, string expected, Guid id, bool streaming, bool classified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DurableReceipt? value;
        if (streaming)
        {
            using SqliteBlob blob = new(connection, "records", "payload", row, readOnly: true);
            using SHA256 hash = SHA256.Create();
            using CryptoStream input = new(blob, hash, CryptoStreamMode.Read);
            value = JsonSerializer.Deserialize<DurableReceipt>(input);
            if (input.ReadByte() != -1 || !string.Equals(Convert.ToHexString(hash.Hash!), expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存履歴の内容が破損しています。");
            }
        }
        else
        {
            using SqliteCommand command = new("SELECT payload FROM records WHERE rowid=$row", connection, transaction);
            command.Parameters.AddWithValue("$row", row);
            string payload = (string)command.ExecuteScalar()!;
            SqlitePayload.Verify(payload, expected);
            value = JsonSerializer.Deserialize<DurableReceipt>(payload);
        }
        if (value is null || value.OperationId != id || classified && !value.Entries.All(entry => DurableRepository.IsShared(entry.Scope)))
        {
            throw new InvalidDataException("共有履歴の識別または保存範囲が不正です。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }
}
