using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>単一SQLiteの取込はDB取引を復旧境界とし、同じ全本文の別journalを作りません。</summary>
internal static class SqliteRoutedImport
{
    /// <summary>所有先と確定結果を一括してcommitし、例外・取消では双方をロールバックします。</summary>
    /// <returns>全変更を耐久確定した後の競合状態です。</returns>
    internal static bool Apply(string path, RepositorySnapshot snapshot, IReadOnlyDictionary<Guid, RoutingOwner> existingOwners,
        EStorageBackend backend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        RepositoryDocument document = SqliteSnapshot.Import(session, snapshot, cancellationToken);
        bool merged = RepositoryMerge.Merge(document, snapshot);
        string scope = "peer:persistence-routing";
        string[] addresses = (snapshot.OperationOwners ?? []).Select(owner => DurableRepository.GetAddress(scope, "owner:" + owner.OperationId.ToString("N"))).ToArray();
        Dictionary<string, DurableEntry> stored = session.List<DurableEntry>("entry", scope, keys: addresses, cancellationToken: cancellationToken).ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (PersistenceOperationOwner owner in snapshot.OperationOwners ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoutingOwner? expected = existingOwners.GetValueOrDefault(owner.OperationId);
            string key = "owner:" + owner.OperationId.ToString("N");
            string address = DurableRepository.GetAddress(scope, key);
            DurableEntry? entry = stored.GetValueOrDefault(key);
            RoutingOwner? current = entry?.Value?.Json.Deserialize<RoutingOwner>(ContractJson.Options);
            if (!string.Equals(ContractJson.Hash(current), ContractJson.Hash(expected), StringComparison.Ordinal))
            {
                throw new InvalidDataException("同期中に操作の論理所有先が変更されました。");
            }
            RoutingOwner updated = new(owner, backend, current?.Result);
            if (!string.Equals(ContractJson.Hash(current), ContractJson.Hash(updated), StringComparison.Ordinal))
            {
                document.Entries[address] = new(scope, key, Guid.NewGuid().ToString("N"), NodeValue.FromObject(updated));
            }
        }
        session.Save(document);
        Recovery.RecoveryRecordLedger.RegisterSnapshot(session, snapshot, cancellationToken);
        History.SqliteHistoryRestore.PromoteProtected(session, snapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return merged;
    }
}
