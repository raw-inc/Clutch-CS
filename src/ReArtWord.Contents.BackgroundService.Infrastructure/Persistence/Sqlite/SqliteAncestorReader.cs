using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>祖先の比較で要求された版だけを読み、無関係な履歴のDTOや変更前文字列を保持しません。</summary>
internal static class SqliteAncestorReader
{
    /// <summary>通常の操作ID版は索引で探し、独自の旧版識別子だけ内容検索へ戻します。</summary>
    /// <returns>同じscope・key・versionの、検証した候補です。</returns>
    internal static IReadOnlyList<DurableEntry> Read(SqliteConnection connection, SqliteTransaction transaction, SqliteHistoryReader history,
        Func<Guid, DurableReceipt?> readReceipt, string scope, string key, IReadOnlyCollection<string> versions, CancellationToken cancellationToken)
    {
        List<DurableEntry> result = [];
        HashSet<string> unresolved = new(versions, StringComparer.Ordinal);
        string operationVersionFormat = "N";
        foreach (string version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(version, operationVersionFormat, out Guid id))
            {
                continue;
            }
            DurableReceipt? receipt = readReceipt(id);
            if (receipt is not null)
            {
                DurableEntry[] matches = Select(receipt, scope, key, [version]).ToArray();
                result.AddRange(matches);
                if (matches.Length != 0)
                {
                    unresolved.Remove(version);
                }
            }
        }
        if (unresolved.Count == 0)
        {
            return result;
        }
        using SqliteCommand command = new("""
            SELECT payload,hash FROM records WHERE kind='receipt'
            AND EXISTS(SELECT 1 FROM json_each(records.payload,'$.Entries') AS item
                WHERE json_extract(item.value,'$.Scope')=$scope AND json_extract(item.value,'$.Key')=$key
                  AND json_extract(item.value,'$.Version') IN (SELECT value FROM json_each($versions)))
            """, connection, transaction);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$versions", JsonSerializer.Serialize(unresolved));
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string payload = reader.GetString(0);
                SqlitePayload.Verify(payload, reader.GetString(1));
                DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(payload) ?? throw new InvalidDataException("保存履歴が不正です。");
                result.AddRange(Select(receipt, scope, key, unresolved));
            }
        }
        foreach (HistoryRow row in history.List("receipt", valueScopes: new HashSet<string>(StringComparer.Ordinal) { scope }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(row.Payload) ?? throw new InvalidDataException("保存履歴が不正です。");
            result.AddRange(Select(receipt, scope, key, unresolved));
        }
        return result;
    }

    /// <summary>同じ操作に含まれる別用途や別版を、要求された親候補へ混ぜません。</summary>
    /// <returns>要求された論理項目の候補だけです。</returns>
    private static IEnumerable<DurableEntry> Select(DurableReceipt receipt, string scope, string key, IReadOnlyCollection<string> versions)
        => receipt.Entries.Where(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, key, StringComparison.Ordinal)
            && versions.Contains(entry.Version, StringComparer.Ordinal));
}
