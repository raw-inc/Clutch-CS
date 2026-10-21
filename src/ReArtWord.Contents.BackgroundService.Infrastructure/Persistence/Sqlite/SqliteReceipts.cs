using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

// LEGACY-FORMAT: receipt-scope-v1 — 旧writerの未分類結果は分類を補完し、旧writerのサポート終了後に補完・フォールバックを撤去します。
/// <summary>共有履歴を索引で選択し、ローカルの大きな要求を同期用に復号しません。</summary>
internal static class SqliteReceipts
{
    /// <summary>旧失敗を補完できる候補だけについて、現在値と祖先証拠を同じ取引へ読みます。</summary>
    internal static void LoadReconciliationEvidence(SqliteSession session, RepositoryDocument document, IEnumerable<DurableReceipt> incoming, CancellationToken cancellationToken)
    {
        DurableEntry[] evidence = incoming.Where(receipt => ReceiptReconciliation.CanReconcile(document.Receipts.GetValueOrDefault(receipt.OperationId), receipt)).SelectMany(receipt => receipt.Entries).ToArray();
        foreach (DurableEntry entry in session.List<DurableEntry>("entry", keys: evidence.Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key)).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken: cancellationToken))
        {
            document.Entries[DurableRepository.GetAddress(entry.Scope, entry.Key)] = entry;
        }
        HashSet<string> knownConflicts = document.Conflicts.Select(Contracts.Serialization.ContractJson.Hash).ToHashSet(StringComparer.Ordinal);
        foreach (string scope in evidence.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal))
        {
            foreach (DurableEntry conflict in session.List<DurableEntry>("conflict", scope, cancellationToken: cancellationToken))
            {
                if (knownConflicts.Add(Contracts.Serialization.ContractJson.Hash(conflict)))
                {
                    document.Conflicts.Add(conflict);
                }
            }
        }
        SqliteCausality.Load(session, document, evidence.Select(entry => (entry.Scope, entry.Key)));
    }

    /// <summary>対象操作だけを一つの読取り取引で照合します。</summary>
    /// <returns>未保存を除いた元の結果です。</returns>
    internal static IReadOnlyDictionary<Guid, DurableReceipt> ReadStored(string path, IReadOnlyCollection<Guid> operationIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return ReadStored(session, operationIds, cancellationToken);
    }

    /// <summary>一致を再利用できない結果もブロック順で読み、消去済み操作を未実行へ戻しません。</summary>
    /// <returns>現在行を優先し、保存済み結果と失効結果だけを含む集合です。</returns>
    internal static IReadOnlyDictionary<Guid, DurableReceipt> ReadStored(SqliteSession session, IReadOnlyCollection<Guid> operationIds, CancellationToken cancellationToken)
    {
        HashSet<Guid> requested = new(operationIds);
        Dictionary<Guid, DurableReceipt> receipts = [];
        if (requested.Count == 0)
        {
            return receipts;
        }
        string[] keys = requested.Select(id => id.ToString("N")).ToArray();
        foreach (DurableReceipt receipt in session.List<DurableReceipt>("receipt", keys: keys, cancellationToken: cancellationToken))
        {
            if (!requested.Contains(receipt.OperationId) || !receipts.TryAdd(receipt.OperationId, receipt))
            {
                throw new InvalidDataException("保存履歴の操作識別が一致しません。");
            }
        }
        foreach (Guid id in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receipts.ContainsKey(id))
            {
                continue;
            }
            DurableReceipt? retired = session.Read<DurableReceipt>("receipt", id.ToString("N"));
            if (retired is not null)
            {
                receipts.Add(id, retired);
            }
        }
        return receipts;
    }

    /// <summary>旧版で未使用だったreceipt.scopeに索引用分類を補います。payload/hashは変更しません。</summary>
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteReceiptFingerprint.Initialize(connection, transaction);
        using SqliteCommand command = new("""
            UPDATE records SET scope=CASE
            WHEN NOT EXISTS(SELECT 1 FROM json_each(records.payload,'$.Entries') AS item
                WHERE substr(json_extract(item.value,'$.Scope'),1,5)='peer:'
                   OR substr(json_extract(item.value,'$.Scope'),1,9)='receipts:'
                   OR substr(json_extract(item.value,'$.Scope'),1,8)='cluster:') THEN 'shared'
            WHEN EXISTS(SELECT 1 FROM json_each(records.payload,'$.Entries') AS item
                WHERE substr(json_extract(item.value,'$.Scope'),1,5)<>'peer:'
                  AND substr(json_extract(item.value,'$.Scope'),1,9)<>'receipts:'
                  AND substr(json_extract(item.value,'$.Scope'),1,8)<>'cluster:') THEN 'mixed'
            ELSE 'local' END WHERE kind='receipt' AND scope IS NULL
            """, connection, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>空結果も従来どおり共有候補とし、混在操作のローカル部分を他Peerへ漏らしません。</summary>
    /// <returns>既存スコープ索引で使用するローカル分類です。</returns>
    internal static string Scope(DurableReceipt receipt)
    {
        if (receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope)))
        {
            return "shared";
        }
        if (receipt.Entries.Any(entry => DurableRepository.IsShared(entry.Scope)))
        {
            return "mixed";
        }
        return "local";
    }

    /// <summary>旧版が後から書いた未分類行も扱い、採用する行のチェックサムを必ず検査します。</summary>
    /// <returns>同期または予約照合に必要な履歴です。</returns>
    internal static IReadOnlyList<DurableReceipt> Read(SqliteConnection connection, SqliteTransaction transaction,
        Action<string, string> remember, bool exportOnly, IReadOnlySet<string>? localScopes)
    {
        using SqliteCommand command = new("SELECT key,payload,hash FROM records WHERE kind='receipt' AND (scope='shared' OR scope IS NULL)", connection, transaction);
        if (!exportOnly)
        {
            command.CommandText = "SELECT key,payload,hash FROM records WHERE kind='receipt' AND (scope IN ('shared','mixed') OR scope IS NULL)";
        }
        if (localScopes is not null)
        {
            command.CommandText = """
                SELECT key,payload,hash FROM records WHERE kind='receipt'
                AND EXISTS(SELECT 1 FROM json_each(records.payload,'$.Entries') AS item
                    WHERE json_extract(item.value,'$.Scope') IN (SELECT value FROM json_each($scopes)))
                """;
            command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(localScopes));
        }
        using SqliteDataReader reader = command.ExecuteReader();
        List<DurableReceipt> receipts = [];
        while (reader.Read())
        {
            string payload = reader.GetString(1);
            SqlitePayload.Verify(payload, reader.GetString(2));
            DurableReceipt receipt = JsonSerializer.Deserialize<DurableReceipt>(payload) ?? throw new InvalidDataException("保存履歴が不正です。");
            bool selected = receipt.Entries.Count == 0 || receipt.Entries.Any(entry => DurableRepository.IsShared(entry.Scope));
            if (exportOnly)
            {
                selected = receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope));
            }
            if (localScopes is not null)
            {
                selected = receipt.Entries.Any(entry => localScopes.Contains(entry.Scope));
            }
            if (selected)
            {
                remember(reader.GetString(0), payload);
                receipts.Add(receipt);
            }
        }
        return receipts;
    }
}
