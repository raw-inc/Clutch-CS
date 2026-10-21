using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>同じ履歴は本文指紋で照合し、JSON表現だけが異なる場合も同じ取引で一度だけ復元します。</summary>
internal static class SqliteReceiptMatch
{
    /// <summary>現在行を優先し、圧縮された候補はブロック順に一度ずつ検証します。</summary>
    /// <returns>未保存・不一致・失効を一致と扱わない、確認済み操作IDです。</returns>
    internal static IReadOnlySet<Guid> Read(SqliteConnection connection, SqliteTransaction transaction, SqliteHistoryReader history,
        IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken)
    {
        HashSet<Guid> matched = [];
        Dictionary<string, DurableReceipt> archived = new(StringComparer.Ordinal);
        foreach (DurableReceipt receipt in receipts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool? current = ReadCurrent(connection, transaction, receipt);
            if (current == true)
            {
                matched.Add(receipt.OperationId);
            }
            else
            {
                if (current is null)
                {
                    archived.TryAdd(receipt.OperationId.ToString("N"), receipt);
                }
            }
        }
        if (archived.Count != 0)
        {
            foreach (HistoryRow row in history.List("receipt", keys: archived.Keys))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DurableReceipt receipt = archived[row.Key];
                if (string.Equals(Fingerprint(receipt), row.Hash, StringComparison.OrdinalIgnoreCase)
                    || Equivalent(JsonSerializer.Deserialize<DurableReceipt>(row.Payload), receipt))
                {
                    matched.Add(receipt.OperationId);
                }
            }
        }
        return matched;
    }

    /// <summary>実バイト列のチェックサムを検証し、同じ標準JSONなら大きな複製なしで一致を返します。</summary>
    /// <returns>同一内容ならtrue、不一致ならfalse、現在行がなければ圧縮履歴を確認するnullです。</returns>
    private static bool? ReadCurrent(SqliteConnection connection, SqliteTransaction transaction, DurableReceipt receipt)
    {
        using SqliteCommand command = new("PRAGMA encoding", connection, transaction);
        string utf8 = "UTF-8";
        if (!string.Equals(command.ExecuteScalar() as string, utf8, StringComparison.Ordinal))
        {
            return false;
        }
        command.CommandText = "SELECT rowid,hash FROM records WHERE kind='receipt' AND key=$id";
        command.Parameters.AddWithValue("$id", receipt.OperationId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        Span<byte> stored = stackalloc byte[32];
        using (SqliteBlob blob = new(connection, "records", "payload", reader.GetInt64(0), readOnly: true))
        {
            _ = SHA256.HashData(blob, stored);
        }
        if (!string.Equals(Convert.ToHexString(stored), reader.GetString(1), StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存履歴の内容が破損しています。");
        }
        if (string.Equals(Fingerprint(receipt), Convert.ToHexString(stored), StringComparison.Ordinal))
        {
            return true;
        }
        using SqliteBlob content = new(connection, "records", "payload", reader.GetInt64(0), readOnly: true);
        return Equivalent(JsonSerializer.Deserialize<DurableReceipt>(content), receipt);
    }

    /// <summary>本文検証後だけ、保存形式の空白・プロパティ順などを既存mergeと同じ基準で比較します。</summary>
    /// <returns>確定結果が同じかです。内容不一致を一致として再利用しません。</returns>
    private static bool Equivalent(DurableReceipt? stored, DurableReceipt incoming)
    {
        if (stored is null)
        {
            throw new InvalidDataException("保存履歴の内容が不正です。");
        }
        return string.Equals(ContractJson.Hash(stored), ContractJson.Hash(incoming), StringComparison.Ordinal);
    }

    /// <summary>保存時と同じJSON表現を、全文バイト配列を作らず照合します。</summary>
    /// <returns>検証済み保存本文と比較するSHA-256です。</returns>
    private static string Fingerprint(DurableReceipt receipt)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using HashBufferWriter buffer = new(hash);
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = 64 }))
        {
            JsonSerializer.Serialize(writer, receipt);
        }
        Span<byte> incoming = stackalloc byte[32];
        _ = hash.GetHashAndReset(incoming);
        return Convert.ToHexString(incoming);
    }
}
