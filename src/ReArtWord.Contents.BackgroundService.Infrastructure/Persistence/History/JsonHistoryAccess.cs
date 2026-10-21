using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>JSONの圧縮済み本文も、通常の参照・CASと同じ内容で扱います。</summary>
internal static class JsonHistoryAccess
{
    /// <summary>種別とキーが衝突しない履歴識別子です。</summary>
    /// <returns>保存内の一意キーです。</returns>
    internal static string Address(string kind, string key) => kind + ":" + key;

    /// <summary>通常行がないときだけ圧縮された一件を読みます。</summary>
    /// <returns>元の型の本文、未保持ならnullです。</returns>
    internal static T? Read<T>(RepositoryDocument document, string kind, string key)
    {
        JsonHistoryRecord? record = document.History?.Archives.GetValueOrDefault(Address(kind, key));
        if (record is null)
        {
            return default;
        }
        HistoryRow row = HistoryBlockCodec.Decode(record.Data, record.Length, record.Hash).Single();
        if (!string.Equals(row.Kind, kind, StringComparison.Ordinal) || !string.Equals(row.Key, key, StringComparison.Ordinal))
        {
            throw new InvalidDataException("圧縮履歴の識別子が一致しません。");
        }
        return JsonSerializer.Deserialize<T>(row.Payload);
    }

    /// <summary>同じ操作の結果を圧縮の有無にかかわらず照合します。</summary>
    /// <returns>元結果または削除後の確認不能です。</returns>
    internal static DurableReceipt? Receipt(RepositoryDocument document, Guid operationId)
        => document.Receipts.GetValueOrDefault(operationId) ?? Read<DurableReceipt>(document, "receipt", operationId.ToString("N"))
            ?? Expired(document, operationId);

    /// <summary>未完了の予約を時間だけで切り捨てません。</summary>
    /// <returns>再実行を拒否する確認不能です。</returns>
    private static DurableReceipt? Expired(RepositoryDocument document, Guid operationId)
    {
        if (document.Prepared.ContainsKey(operationId))
        {
            return null;
        }
        return RepositoryHistoryGuard.Expired(operationId, RepositoryHistoryGuard.Read(document), document.History?.RetiredOperations.GetValueOrDefault(operationId));
    }

    /// <summary>指定した用途に限って圧縮済み本文を列挙します。</summary>
    /// <returns>通常行と同じ型の保存値です。</returns>
    internal static DurableEntry[] List(RepositoryDocument document, string scope)
    {
        List<DurableEntry> result = document.Entries.Values.Where(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal)).ToList();
        string entryKind = "entry";
        foreach (JsonHistoryRecord row in document.History?.Archives.Values.AsEnumerable() ?? [])
        {
            if (string.Equals(row.Kind, entryKind, StringComparison.Ordinal) && string.Equals(row.Scope, scope, StringComparison.Ordinal)
                && !document.Entries.ContainsKey(row.Key))
            {
                result.Add(Read<DurableEntry>(document, row.Kind, row.Key)!);
            }
        }
        return result.ToArray();
    }

    /// <summary>CASの比較に必要な行だけを通常処理へ展開します。</summary>
    internal static void Hydrate(RepositoryDocument document, Guid operationId, IEnumerable<DurableMutation> mutations)
    {
        DurableReceipt? receipt = Receipt(document, operationId);
        if (receipt is not null && !document.Prepared.ContainsKey(operationId))
        {
            document.Receipts.TryAdd(operationId, receipt);
        }
        foreach (DurableMutation mutation in mutations)
        {
            string address = DurableRepository.GetAddress(mutation.Scope, mutation.Key);
            DurableEntry? entry = Read<DurableEntry>(document, "entry", address);
            if (entry is not null)
            {
                document.Entries.TryAdd(address, entry);
            }
        }
    }

    /// <summary>比較のためだけに展開した同じ本文を通常領域へ重複保存しません。</summary>
    internal static void Reconcile(RepositoryDocument document)
    {
        if (document.History is null)
        {
            return;
        }
        string receiptKind = "receipt", entryKind = "entry", idFormat = "N";
        foreach (DurableReceipt receipt in document.Receipts.Values.ToArray())
        {
            if (string.Equals(receipt.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal)
                || Same(document, receiptKind, receipt.OperationId.ToString(idFormat), receipt))
            {
                document.Receipts.Remove(receipt.OperationId);
            }
        }
        foreach ((string key, DurableEntry entry) in document.Entries.ToArray())
        {
            if (Same(document, entryKind, key, entry))
            {
                document.Entries.Remove(key);
            }
        }
    }

    /// <summary>更新された冷たい行だけを通常領域へ戻します。</summary>
    /// <returns>同じ本文が圧縮済みかです。</returns>
    private static bool Same<T>(RepositoryDocument document, string kind, string key, T value)
    {
        string address = Address(kind, key);
        if (!document.History!.Archives.TryGetValue(address, out JsonHistoryRecord? archived))
        {
            return false;
        }
        HistoryRow row = HistoryBlockCodec.Decode(archived.Data, archived.Length, archived.Hash).Single();
        if (string.Equals(SqlitePayload.Hash(JsonSerializer.Serialize(value)), row.Hash, StringComparison.Ordinal))
        {
            return true;
        }
        document.History.Archives.Remove(address);
        document.History.Deadlines.Remove(address);
        return false;
    }
}
