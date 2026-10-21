using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>複数保存媒体の固定Importをローカル操作履歴の完了まで非公開にします。</summary>
internal sealed class RoutingSynchronization(string directory, RepositoryRoutes routes, RoutingState state)
{
    private string? previousVersions;
    private string? previousSharedVersion;

    /// <summary>複数保存先を同じ論理範囲へまとめます。</summary>
    /// <returns>現在状態と保持境界を含む固定した書出しです。</returns>
    internal Task<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
        => new RoutingSnapshotExport(routes, state).ExportAsync(cancellationToken);

    /// <summary>媒体を跨いでも過去の操作結果は復元せず現在状態を確認します。</summary>
    /// <returns>共有版の判断に使用する現在内容です。</returns>
    internal Task<RepositorySnapshot> ReadCurrentAsync(CancellationToken cancellationToken = default)
        => new RoutingSnapshotExport(routes, state).ReadCurrentAsync(cancellationToken);

    /// <summary>変更時だけ全候補から既存のcanonical ハッシュを計算します。</summary>
    /// <returns>物理媒体構成に依存しない共有版です。</returns>
    internal async Task<string> VersionAsync(CancellationToken cancellationToken = default)
    {
        List<string> versions = [];
        foreach (IDurableRepository store in routes.SharedStores)
        {
            versions.Add(await store.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false));
        }
        if (versions.Count == 1)
        {
            return versions[0];
        }
        string fingerprint = ContractJson.Hash(versions);
        if (!string.Equals(previousVersions, fingerprint, StringComparison.Ordinal) || previousSharedVersion is null)
        {
            RepositorySnapshot snapshot = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            previousSharedVersion = RepositoryStateVersion.Hash(snapshot);
            previousVersions = fingerprint;
        }
        return previousSharedVersion;
    }

    /// <summary>原子的に扱えない操作記録を先に拒否し、固定内容だけをstagingします。</summary>
    /// <returns>取込み完了後に競合がなければtrueです。</returns>
    // 抑制理由: FlushAsyncはOSの永続flushを保証しないため、操作履歴公開前の内容をFlush(true)で同期します。
    [SuppressMessage("Performance", "CA1849", Justification = "未完了Importの内容を物理保存してからjournalを公開します。")]
    internal async Task<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.DeferredHistory is not null && (routes.Stores.Count != 1 || routes.Stores[routes.Default] is not SqliteRepository))
        {
            throw new NotSupportedException("段階同期は単一SQLiteの保存構成を必要とします。");
        }
        PartitionedSnapshot partitioned = await PartitionAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (routes.Stores.Count == 1 && routes.Stores[routes.Default] is SqliteRepository sqlite
            && await state.PendingAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            bool merged = sqlite.ImportRouted(partitioned.Partitions[routes.Default], partitioned.ExistingOwners, routes.Default, cancellationToken);
            previousVersions = null;
            return merged;
        }
        string candidate = Path.Combine(directory, "routing-next.json");
        ValidatePath(candidate);
        string hash;
        using (FileStream output = new(candidate, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true))
        {
            await JsonSerializer.SerializeAsync(output, snapshot, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(true);
            output.Position = 0;
            hash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false));
        }
        string path = Path.Combine(directory, "routing-import.json");
        ValidatePath(path);
        string? pending = await state.PendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending is not null && !string.Equals(hash, pending, StringComparison.Ordinal))
        {
            File.Delete(candidate);
            throw new IOException("未完了Importへ別のsnapshotを継ぎ足せません。");
        }
        if (pending is null)
        {
            File.Move(candidate, path, true);
            await state.WriteAsync("import", NodeValue.FromObject(hash), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            File.Delete(candidate);
        }
        return await RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同じ固定スナップショットを再開し、全媒体完了前の通常アクセスを閉じたままにします。</summary>
    /// <returns>全媒体を取り込んだ後に競合がないかです。</returns>
    internal async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        string? expected = await state.PendingAsync(cancellationToken).ConfigureAwait(false);
        if (expected is null)
        {
            return true;
        }
        string path = Path.Combine(directory, "routing-import.json");
        ValidatePath(path);
        RepositorySnapshot snapshot;
        using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("未完了Importの内容hashが一致しません。");
            }
            input.Position = 0;
            snapshot = await JsonSerializer.DeserializeAsync<RepositorySnapshot>(input, ContractJson.Options, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("未完了Importの内容がありません。");
        }
        PartitionedSnapshot partitioned = await PartitionAsync(snapshot, cancellationToken).ConfigureAwait(false);
        bool merged = true;
        foreach ((EStorageBackend backend, RepositorySnapshot partition) in partitioned.Partitions)
        {
            foreach (PersistenceOperationOwner owner in partition.OperationOwners ?? [])
            {
                RoutingOwner? existing = partitioned.ExistingOwners.GetValueOrDefault(owner.OperationId);
                if (existing is null || existing.Backend != backend || !string.Equals(ContractJson.Hash(existing.Operation), ContractJson.Hash(owner), StringComparison.Ordinal))
                {
                    await state.SaveOwnerAsync(new(owner, backend, existing?.Result), cancellationToken).ConfigureAwait(false);
                }
            }
            bool complete = await routes.Stores[backend].ImportAsync(partition, cancellationToken).ConfigureAwait(false);
            merged &= complete;
        }
        await state.WriteAsync("import", NodeValue.Null, cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        previousVersions = null;
        return merged;
    }

    /// <summary>元操作全体がこのPeerでも一媒体へ入ることと指紋を確認します。</summary>
    /// <returns>同一操作の履歴を分割しない取込み集合です。</returns>
    private async Task<PartitionedSnapshot> PartitionAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        foreach (IGrouping<Guid, DurableReceipt> duplicates in snapshot.Receipts.GroupBy(receipt => receipt.OperationId).Where(group => 1 < group.Count()))
        {
            string first = ContractJson.Hash(duplicates.First());
            if (duplicates.Skip(1).Any(receipt => !string.Equals(first, ContractJson.Hash(receipt), StringComparison.Ordinal)))
            {
                throw new InvalidDataException("同期内の同じ操作IDに異なる結果があります。");
            }
        }
        Dictionary<Guid, PersistenceOperationOwner> metadata = (snapshot.OperationOwners ?? []).ToDictionary(owner => owner.OperationId);
        Dictionary<Guid, RoutingOwner> existingOwners = (await state.ReadOwnersAsync(snapshot.Receipts.Select(receipt => receipt.OperationId), cancellationToken).ConfigureAwait(false))
            .ToDictionary(owner => owner.Operation.OperationId);
        DurableReceipt[] shared = snapshot.Receipts.Where(receipt => (metadata.GetValueOrDefault(receipt.OperationId)?.Scopes
            ?? existingOwners.GetValueOrDefault(receipt.OperationId)?.Operation.Scopes ?? receipt.Entries.Select(entry => entry.Scope).ToArray()).All(DurableRepository.IsShared)).ToArray();
        Dictionary<EStorageBackend, IReadOnlySet<Guid>> matching = [];
        Dictionary<EStorageBackend, IReadOnlyDictionary<Guid, DurableReceipt>> compared = [];
        foreach ((EStorageBackend backend, IDurableRepository store) in routes.Stores)
        {
            matching[backend] = store switch
            {
                SqliteRepository sqlite => await sqlite.MatchingReceiptsAsync(shared, cancellationToken).ConfigureAwait(false),
                JsonFileRepository json => await json.MatchingReceiptsAsync(shared, cancellationToken).ConfigureAwait(false),
                _ => new HashSet<Guid>(),
            };
            if (store is SqliteRepository sqliteStore)
            {
                Guid[] required = shared.Concat(snapshot.Receipts.Where(IsAborted)).Select(receipt => receipt.OperationId)
                    .Where(id => !matching[backend].Contains(id)).Distinct().ToArray();
                compared[backend] = await sqliteStore.ReadReceiptsAsync(required, cancellationToken).ConfigureAwait(false);
            }
        }
        Dictionary<EStorageBackend, List<DurableEntry>> entries = routes.Stores.Keys.ToDictionary(backend => backend, _ => new List<DurableEntry>());
        Dictionary<EStorageBackend, List<DurableReceipt>> receipts = routes.Stores.Keys.ToDictionary(backend => backend, _ => new List<DurableReceipt>());
        Dictionary<EStorageBackend, List<PersistenceOperationOwner>> owners = routes.Stores.Keys.ToDictionary(backend => backend, _ => new List<PersistenceOperationOwner>());
        foreach (DurableEntry entry in snapshot.Entries)
        {
            if (DurableRepository.IsShared(entry.Scope))
            {
                entries[routes.Backend(entry.Scope)].Add(entry);
            }
        }
        foreach (DurableReceipt incoming in snapshot.Receipts)
        {
            DurableReceipt receipt = incoming;
            string[] scopes = receipt.Entries.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            RoutingOwner? old = existingOwners.GetValueOrDefault(receipt.OperationId);
            PersistenceOperationOwner? owner = metadata.GetValueOrDefault(receipt.OperationId);
            if (owner is null && scopes.Length == 0)
            {
                owner = old?.Operation;
            }
            owner ??= new(receipt.OperationId, receipt.Fingerprint, scopes);
            if (owner.Scopes is null || owner.Fingerprint is null || receipt.Fingerprint is null)
            {
                throw new InvalidDataException("同期操作のscopeまたは指紋がありません。");
            }

            // 保存先を確保してもprepareが成立しなかった場合、取消結果には指紋が残らないことがあります。
            if (IsAborted(receipt) && receipt.Fingerprint.Length == 0 && owner.Fingerprint.Length != 0)
            {
                receipt = receipt with { Fingerprint = owner.Fingerprint };
            }
            if (owner.Scopes.Any(string.IsNullOrWhiteSpace) || !string.Equals(owner.Fingerprint, receipt.Fingerprint, StringComparison.Ordinal) || (scopes.Length != 0 && !new HashSet<string>(owner.Scopes, StringComparer.Ordinal).SetEquals(scopes)))
            {
                throw new InvalidDataException("同期操作のscopeまたは指紋が一致しません。");
            }
            if (await KeepLocalAbortAsync(receipt, old, matching, incoming, compared, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            if (!owner.Scopes.All(DurableRepository.IsShared))
            {
                continue;
            }
            EStorageBackend? selected = routes.Select(owner.Scopes);
            if (!selected.HasValue && receipt.Entries.Count != 0)
            {
                throw new InvalidDataException("同期元の原子的操作がこのPeerの複数媒体へ分かれます。保存用途の割当を整合させてください。");
            }
            EStorageBackend backend = selected ?? routes.Default;
            if (old is not null && (!string.Equals(old.Operation.Fingerprint, owner.Fingerprint, StringComparison.Ordinal) || old.Backend != backend))
            {
                throw new InvalidDataException("同期操作と局所操作の耐久所有先が一致しません。");
            }
            if (old?.Result is not null && !string.Equals(ContractJson.Hash(old.Result), ContractJson.Hash(receipt), StringComparison.Ordinal))
            {
                throw new InvalidDataException("同期操作と局所で確定した拒否結果が異なります。");
            }
            bool retiredReceipt = false;
            foreach ((EStorageBackend backendKey, IDurableRepository store) in routes.Stores)
            {
                if (ReferenceEquals(receipt, incoming) && matching[backendKey].Contains(receipt.OperationId))
                {
                    continue;
                }

                // 補正前の入力と保存本文の一致は検証済みです。補正後も保存済みの元結果として再利用します。
                DurableReceipt? completed = matching[backendKey].Contains(receipt.OperationId) ? incoming
                                                                                               : await ReadExistingAsync(store, compared.GetValueOrDefault(backendKey), receipt.OperationId, cancellationToken).ConfigureAwait(false);
                if (string.Equals(completed?.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal))
                {
                    retiredReceipt = true;
                    if (completed!.Fingerprint.Length != 0 && !string.Equals(completed.Fingerprint, receipt.Fingerprint, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("削除済み操作と同期元の指紋が一致しません。");
                    }
                    continue;
                }
                if (completed is not null && !string.Equals(ContractJson.Hash(completed), ContractJson.Hash(receipt), StringComparison.Ordinal))
                {
                    if (string.Equals(completed.Reason, Recovery.RecoveryRecordLedger.PendingReason, StringComparison.Ordinal))
                    {
                        // 単一SQLiteの取込取引内で、未取得の元検査値と本文を再照合して確定します。
                        continue;
                    }
                    throw new InvalidDataException("同期操作と局所の確定結果が異なります。");
                }
            }
            if (retiredReceipt)
            {
                continue;
            }
            receipts[backend].Add(receipt);
            owners[backend].Add(owner);
        }
        return new(entries.ToDictionary(pair => pair.Key, pair => new RepositorySnapshot(pair.Value, receipts[pair.Key], owners[pair.Key],
            snapshot.CausalEntries?.Where(entry => routes.Backend(entry.Scope) == pair.Key).ToArray(),
            snapshot.LegacyEntries?.Where(entry => routes.Backend(entry.Scope) == pair.Key).ToArray(), snapshot.HistoryBoundary, snapshot.RetiredOperations, snapshot.ProtectedHistory,
            snapshot.RetiredKeys?.Where(key => routes.Backend(key.Scope) == pair.Key).ToArray(), DeferredHistory: snapshot.DeferredHistory,
            DeferredSourcePeer: snapshot.DeferredSourcePeer)), existingOwners);
    }

    /// <summary>準備前と準備後の取消が行き違っても、各媒体に確定済みの取消記録と所有先を維持します。</summary>
    /// <returns>指紋の未取得だけが異なる取消を、保存済みの結果で扱えるかです。</returns>
    private async Task<bool> KeepLocalAbortAsync(DurableReceipt incoming, RoutingOwner? owner, Dictionary<EStorageBackend, IReadOnlySet<Guid>> matching, DurableReceipt original,
        Dictionary<EStorageBackend, IReadOnlyDictionary<Guid, DurableReceipt>> compared, CancellationToken cancellationToken = default)
    {
        if (!IsAborted(incoming) || owner?.Result is not null)
        {
            return false;
        }
        string known = owner?.Operation.Fingerprint ?? string.Empty;
        bool incomplete = owner is not null && !string.Equals(owner.Operation.Fingerprint, incoming.Fingerprint, StringComparison.Ordinal);
        bool hasLocalAbort = false;
        foreach ((EStorageBackend backend, IDurableRepository store) in routes.Stores)
        {
            DurableReceipt? existing = matching[backend].Contains(incoming.OperationId) ? original
                                                                                        : await ReadExistingAsync(store, compared.GetValueOrDefault(backend), incoming.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                continue;
            }
            if (!IsAborted(existing))
            {
                return false;
            }
            hasLocalAbort = true;
            foreach (string fingerprint in new[] { existing.Fingerprint, incoming.Fingerprint })
            {
                if (fingerprint.Length != 0)
                {
                    if (known.Length != 0 && !string.Equals(known, fingerprint, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    known = fingerprint;
                }
            }
            incomplete |= !string.Equals(existing.Fingerprint, incoming.Fingerprint, StringComparison.Ordinal);
        }
        return hasLocalAbort && incomplete;
    }

    /// <summary>一括で確認したSQLiteの結果を再利用し、他の媒体は既存の取得規則を維持します。</summary>
    /// <returns>同じ取込みで比較する保存済み結果です。</returns>
    private static ValueTask<DurableReceipt?> ReadExistingAsync(IDurableRepository store, IReadOnlyDictionary<Guid, DurableReceipt>? known, Guid id, CancellationToken cancellationToken = default)
    {
        if (known is null)
        {
            return store.ReadReceiptAsync(id, cancellationToken);
        }
        return ValueTask.FromResult(known.GetValueOrDefault(id));
    }

    /// <summary>変更を一件も適用していない、明示的な取消結果だけを対象にします。</summary>
    /// <returns>準備済みデータの取消が確定しているかです。</returns>
    private static bool IsAborted(DurableReceipt receipt)
    {
        string aborted = "Persistence.Aborted";
        return receipt.Fingerprint is not null && receipt.Outcome == EOutcome.Failed && receipt.Entries.Count == 0 && string.Equals(receipt.Reason, aborted, StringComparison.Ordinal);
    }

    /// <summary>stagingとcleanupをこの保存先のルートの通常ファイルに限ります。</summary>
    private void ValidatePath(string path) => new FileAccessPolicy(new FileSystemOptions([directory])).ValidatePath(path);

    /// <summary>検証した所有情報を保存段階へ引き継ぎ、既存情報を重ねて書きません。</summary>
    private sealed record PartitionedSnapshot(IReadOnlyDictionary<EStorageBackend, RepositorySnapshot> Partitions, IReadOnlyDictionary<Guid, RoutingOwner> ExistingOwners);

}
