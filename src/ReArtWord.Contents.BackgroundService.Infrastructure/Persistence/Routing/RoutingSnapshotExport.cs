using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>用途ごとの実保存先を一つの論理共有スナップショットへ投影します。</summary>
internal sealed class RoutingSnapshotExport(RepositoryRoutes routes, RoutingState state)
{
    /// <summary>単一共有媒体では、原子的操作の所有先を検証してから本文参照だけを渡します。</summary>
    /// <returns>固定した媒体を使用する処理結果です。</returns>
    internal async Task<T> ReadAsync<T>(Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken = default)
    {
        if (routes.SharedStores.Count != 1)
        {
            RepositorySnapshot snapshot = await ExportAsync(cancellationToken).ConfigureAwait(false);
            return await read(new(snapshot with { Receipts = [] }, snapshot.Receipts.Select(SnapshotReceipt.FromValue).ToArray())).ConfigureAwait(false);
        }
        return await SnapshotExport.ReadAsync(routes.SharedStores[0], async source =>
        {
            Dictionary<Guid, RoutingOwner> known = (await state.ReadOwnersAsync(source.Receipts.Select(item => item.OperationId), cancellationToken).ConfigureAwait(false))
                .ToDictionary(owner => owner.Operation.OperationId);
            Dictionary<Guid, SnapshotReceipt> receipts = [];
            foreach (SnapshotReceipt receipt in source.Receipts)
            {
                if (!known.TryGetValue(receipt.OperationId, out RoutingOwner? owner) || owner.Operation.Scopes.All(DurableRepository.IsShared))
                {
                    receipts.Add(receipt.OperationId, receipt);
                }
            }
            foreach (RoutingOwner owner in await state.SharedResultsAsync(cancellationToken).ConfigureAwait(false))
            {
                known[owner.Operation.OperationId] = owner;
                if (owner.Result is null || !owner.Operation.Scopes.All(DurableRepository.IsShared))
                {
                    continue;
                }
                if (receipts.TryGetValue(owner.Operation.OperationId, out SnapshotReceipt? existing)
                    && !string.Equals(existing.Hash(), ContractJson.Hash(owner.Result), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("同じ操作IDの保存結果が異なります。");
                }
                receipts[owner.Operation.OperationId] = SnapshotReceipt.FromValue(owner.Result);
            }
            List<PersistenceOperationOwner> owners = [];
            foreach (SnapshotReceipt receipt in receipts.Values)
            {
                if (known.TryGetValue(receipt.OperationId, out RoutingOwner? owner))
                {
                    owners.Add(owner.Operation);
                }
                else
                {
                    DurableReceipt value = receipt.Read();
                    owners.Add(new(value.OperationId, value.Fingerprint, value.Entries.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
                }
            }
            RepositorySnapshot header = source.Snapshot with
            {
                OperationOwners = owners, CausalEntries = source.Snapshot.CausalEntries ?? [], LegacyEntries = source.Snapshot.LegacyEntries ?? [],
                RetiredOperations = source.Snapshot.RetiredOperations ?? [], ProtectedHistory = source.Snapshot.ProtectedHistory ?? [],
                RetiredKeys = source.Snapshot.RetiredKeys ?? [], PendingOperations = source.Snapshot.PendingOperations ?? [],
            };
            return await read(new(header, receipts.Values.ToArray(), source.ReadHistoricalReceipt)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共有用途の候補・履歴・論理所有先を一つのスナップショットにします。</summary>
    /// <returns>同じscope/keyの分岐versionを落とさない共有内容です。</returns>
    internal Task<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
        => ExportCoreAsync(true, cancellationToken);

    /// <summary>確定済み操作の本文と所有履歴を展開せず現在状態をまとめます。</summary>
    /// <returns>共有版と復旧判断に必要な現在値です。</returns>
    internal Task<RepositorySnapshot> ReadCurrentAsync(CancellationToken cancellationToken = default)
        => ExportCoreAsync(false, cancellationToken);

    /// <summary>用途別の現在状態を統合し、明示的な書出しだけに確定結果を含めます。</summary>
    /// <returns>競合と未確定予約を維持した共有内容です。</returns>
    private async Task<RepositorySnapshot> ExportCoreAsync(bool includeReceipts, CancellationToken cancellationToken = default)
    {
        List<DurableEntry> entries = [];
        List<RepositoryCausalEntry> causal = [];
        List<RepositoryLegacyEntry> legacy = [];
        List<RetiredOperation> retired = [];
        List<string> protectedHistory = [];
        List<RetiredHistoryKey> retiredKeys = [];
        List<Guid> pending = [];
        RepositoryDocument boundary = new();
        Dictionary<Guid, DurableReceipt> receipts = [];
        List<DurableReceipt> candidates = [];
        foreach (IDurableRepository store in routes.SharedStores)
        {
            RepositorySnapshot snapshot;
            if (includeReceipts)
            {
                snapshot = await store.ExportAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = await store.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(false);
            }
            entries.AddRange(snapshot.Entries);
            causal.AddRange(snapshot.CausalEntries ?? []);
            legacy.AddRange(snapshot.LegacyEntries ?? []);
            retired.AddRange(snapshot.RetiredOperations ?? []);
            protectedHistory.AddRange(snapshot.ProtectedHistory ?? []);
            retiredKeys.AddRange(snapshot.RetiredKeys ?? []);
            pending.AddRange(snapshot.PendingOperations ?? []);
            if (snapshot.HistoryBoundary is { } retained)
            {
                RepositoryHistoryGuard.Merge(boundary, retained);
            }
            candidates.AddRange(snapshot.Receipts);
        }
        Dictionary<Guid, RoutingOwner> knownOwners = [];
        if (includeReceipts)
        {
            knownOwners = (await state.ReadOwnersAsync(candidates.Select(receipt => receipt.OperationId), cancellationToken).ConfigureAwait(false))
                .ToDictionary(owner => owner.Operation.OperationId);
        }
        foreach (DurableReceipt receipt in candidates)
        {
            RoutingOwner? owner = knownOwners.GetValueOrDefault(receipt.OperationId);
            if (owner is null || owner.Operation.Scopes.All(DurableRepository.IsShared))
            {
                AddReceipt(receipts, receipt);
            }
        }
        IReadOnlyList<RoutingOwner> sharedResults = [];
        if (includeReceipts)
        {
            sharedResults = await state.SharedResultsAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (RoutingOwner owner in sharedResults)
        {
            knownOwners[owner.Operation.OperationId] = owner;
            if (owner.Result is not null && owner.Operation.Scopes.All(DurableRepository.IsShared))
            {
                AddReceipt(receipts, owner.Result);
            }
        }
        List<PersistenceOperationOwner> owners = [];
        foreach (DurableReceipt receipt in receipts.Values)
        {
            RoutingOwner? owner = knownOwners.GetValueOrDefault(receipt.OperationId);
            owners.Add(owner?.Operation ?? new(receipt.OperationId, receipt.Fingerprint, receipt.Entries.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        return new(entries.Distinct().ToArray(), receipts.Values.ToArray(), owners, causal.Distinct().ToArray(), legacy.Distinct().ToArray(),
            RepositoryHistoryGuard.Read(boundary), retired.DistinctBy(operation => operation.Id).ToArray(), protectedHistory.Distinct(StringComparer.Ordinal).ToArray(), retiredKeys.Distinct().ToArray(), pending.Distinct().ToArray());
    }

    /// <summary>同一操作の異なる結果を時刻等で勝手に選びません。</summary>
    private static void AddReceipt(Dictionary<Guid, DurableReceipt> receipts, DurableReceipt receipt)
    {
        if (receipts.TryGetValue(receipt.OperationId, out DurableReceipt? old) && !string.Equals(ContractJson.Hash(old), ContractJson.Hash(receipt), StringComparison.Ordinal))
        {
            throw new InvalidDataException("同じ操作IDの保存結果が異なります。");
        }
        receipts[receipt.OperationId] = receipt;
    }

}
