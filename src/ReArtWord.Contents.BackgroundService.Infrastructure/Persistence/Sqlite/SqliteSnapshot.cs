using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>同期対象と予約の依存行だけを既存のmerge規則へ渡します。</summary>
internal static class SqliteSnapshot
{
    /// <summary>現在値と必要な場合だけの履歴を、一つの読取り取引で固定します。</summary>
    /// <returns>同じ版から読み出した共有内容です。</returns>
    internal static RepositorySnapshot Read(string path, bool includeReceipts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return Export(session, includeReceipts);
    }

    /// <summary>ローカルの診断・要求履歴をオブジェクト化せず共有内容を取得します。</summary>
    /// <returns>従来と同じ共有値・共有可能な操作履歴です。</returns>
    internal static RepositorySnapshot Export(SqliteSession session, bool includeReceipts = true)
    {
        RetiredHistoryKey[] retired = SqliteHistoryGuard.ExportKeys(session);
        HashSet<(string Scope, string Key)> retiredKeys = retired.Select(key => (key.Scope, key.Key)).ToHashSet();
        DurableEntry[] entries = session.List<DurableEntry>("entry", sharedOnly: true).Concat(session.List<DurableEntry>("conflict", sharedOnly: true))
            .Where(entry => DurableRepository.IsShared(entry.Scope) && !retiredKeys.Contains((entry.Scope, entry.Key))).Distinct().ToArray();
        RepositoryDocument metadata = new();
        session.LoadPrepared(metadata);
        SqliteCausality.Load(session, metadata, entries.Select(entry => (entry.Scope, entry.Key)));
        SqliteHistoryGuard.Load(session, metadata);
        IReadOnlyList<DurableReceipt> receipts = [];
        if (includeReceipts)
        {
            Recovery.RecoveryRecordLedger.RequireComplete(session);
            receipts = session.ReadReceipts(exportOnly: true);
        }
        return new(entries, receipts, CausalEntries: RepositoryCausality.Export(metadata, entries), LegacyEntries: RepositoryLegacyAncestry.Export(metadata, entries),
            HistoryBoundary: RepositoryHistoryGuard.Read(metadata), RetiredOperations: SqliteHistoryGuard.Export(session),
            ProtectedHistory: entries.Where(entry => entry.Value?.Json.ValueKind == System.Text.Json.JsonValueKind.Object
                && HistoryRetentionPolicy.Category("entry", entry.Scope) is { } category && RetainedHistoryEligibility.Protected(session, entry, category))
                .Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version).ToArray(), RetiredKeys: retired,
            PendingOperations: metadata.Prepared.Values.Where(batch => batch.Mutations.Any(mutation => DurableRepository.IsShared(mutation.Scope))).Select(batch => batch.OperationId).ToArray());
    }

    /// <summary>既存の祖先判定、操作ID衝突、混在操作と予約解放を維持して必要行を集めます。</summary>
    /// <returns>取込みで参照・変更する行です。</returns>
    internal static RepositoryDocument Import(SqliteSession session, RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        RepositoryDocument document = new() { ReadAncestors = (scope, key, versions) => session.ReadAncestors(scope, key, versions, cancellationToken) };
        document.Conflicts.AddRange(session.List<DurableEntry>("conflict", cancellationToken: cancellationToken));
        session.LoadPrepared(document);
        string[] addresses = snapshot.Entries.Where(entry => DurableRepository.IsShared(entry.Scope)).Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key))
            .Concat(document.Prepared.Values.SelectMany(prepared => prepared.Mutations.Select(mutation => DurableRepository.GetAddress(mutation.Scope, mutation.Key))))
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (DurableEntry entry in session.List<DurableEntry>("entry", keys: addresses, cancellationToken: cancellationToken))
        {
            document.Entries[DurableRepository.GetAddress(entry.Scope, entry.Key)] = entry;
        }
        SqliteCausality.Load(session, document, document.Entries.Values.Concat(document.Conflicts).Select(entry => (entry.Scope, entry.Key)).ToArray());
        foreach ((Guid id, DurableReceipt receipt) in session.ReuseReceipts(snapshot.Receipts, cancellationToken))
        {
            document.Receipts[id] = receipt;
        }
        IEnumerable<Guid> requiredIds = snapshot.Receipts.Where(receipt => receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope)))
            .Select(receipt => receipt.OperationId).Concat(document.Prepared.Keys).Distinct();
        Guid[] missing = requiredIds.Where(id => !document.Receipts.ContainsKey(id)).ToArray();
        foreach ((Guid id, DurableReceipt existing) in SqliteReceipts.ReadStored(session, missing, cancellationToken))
        {
            if (string.Equals(existing.Reason, Recovery.RecoveryRecordLedger.PendingReason, StringComparison.Ordinal)
                && snapshot.Receipts.FirstOrDefault(receipt => receipt.OperationId == id) is { } incoming)
            {
                Recovery.RecoveryRecordLedger.ValidateIncoming(session, incoming);
                document.Receipts[id] = incoming;
                continue;
            }
            document.Receipts[id] = existing;
        }
        SqliteReceipts.LoadReconciliationEvidence(session, document, snapshot.Receipts, cancellationToken);
        SqliteHistoryGuard.Load(session, document);
        foreach (RetiredHistoryKey key in SqliteHistoryGuard.ExportKeys(session))
        {
            (document.History ??= new()).RetiredKeys.Add(DurableRepository.GetAddress(key.Scope, key.Key));
        }
        return document;
    }

}
