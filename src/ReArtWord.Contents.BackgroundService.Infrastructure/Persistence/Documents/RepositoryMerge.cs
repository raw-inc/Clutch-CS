using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>共有履歴の祖先関係と明示的な競合解消を扱います。</summary>
internal static class RepositoryMerge
{
    /// <summary>通常CASは競合を上書きせず、明示解消だけ全候補集合を比較します。</summary>
    /// <returns>現在の候補集合に対して変更できるか。</returns>
    internal static bool Matches(RepositoryDocument document, DurableMutation mutation, DurableEntry? current)
    {
        DurableEntry[] candidates = document.Conflicts.Where(entry => string.Equals(entry.Scope, mutation.Scope, StringComparison.Ordinal) && string.Equals(entry.Key, mutation.Key, StringComparison.Ordinal)).ToArray();
        if (mutation.ResolveVersions is null)
        {
            return candidates.Length == 0 && string.Equals(current?.Version, mutation.ExpectedVersion, StringComparison.Ordinal);
        }
        HashSet<string> versions = candidates.Select(entry => entry.Version).ToHashSet(StringComparer.Ordinal);
        if (current is not null)
        {
            versions.Add(current.Version);
        }
        return versions.Count != 0 && versions.SetEquals(mutation.ResolveVersions);
    }

    /// <summary>各Peerのローカル現在候補に依存しない同じ解消行を作ります。</summary>
    /// <returns>全候補を親にした確定行です。</returns>
    internal static DurableEntry CreateResolved(Guid operationId, DurableMutation mutation)
    {
        if (mutation.ResolveVersions is null)
        {
            return new(mutation.Scope, mutation.Key, operationId.ToString("N"), mutation.Value, mutation.ExpectedVersion);
        }
        string[] versions = mutation.ResolveVersions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(mutation.Scope, mutation.Key, operationId.ToString("N"), mutation.Value, versions[0], versions);
    }

    /// <summary>変更履歴から包含関係を調べます。</summary>
    /// <returns>非競合ならtrue。</returns>
    public static bool Merge(RepositoryDocument document, RepositorySnapshot snapshot)
    {
        string previousState = RepositoryStateVersion.HashEntries(document.Entries.Values.Concat(document.Conflicts));
        RepositoryCausality.Import(document, snapshot);
        RepositoryLegacyAncestry.Remember(document, snapshot.LegacyEntries ?? []);
        foreach (RetiredHistoryKey key in snapshot.RetiredKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key.Scope) || string.IsNullOrWhiteSpace(key.Key) || !RepositoryHistoryGuard.IsSingleUse(key.Scope))
            {
                throw new InvalidDataException("失効した履歴の種類またはキーが不正です。");
            }
            if (DurableRepository.IsShared(key.Scope))
            {
                (document.History ??= new()).RetiredKeys.Add(DurableRepository.GetAddress(key.Scope, key.Key));
            }
        }
        if (snapshot.HistoryBoundary is { } boundary)
        {
            RepositoryHistoryGuard.Merge(document, boundary);
        }
        foreach (RetiredOperation retired in snapshot.RetiredOperations ?? [])
        {
            if (retired.Shared)
            {
                (document.History ??= new()).RetiredOperations[retired.Id] = retired;
            }
        }
        foreach (DurableReceipt receipt in snapshot.Receipts)
        {
            if (receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope))
                && RepositoryHistoryGuard.Expired(receipt.OperationId, RepositoryHistoryGuard.Read(document), document.History?.RetiredOperations.GetValueOrDefault(receipt.OperationId)) is null)
            {
                document.Receipts.TryAdd(receipt.OperationId, receipt);
            }
        }
        foreach (DurableEntry incoming in snapshot.Entries)
        {
            if (!DurableRepository.IsShared(incoming.Scope) || RepositoryHistoryGuard.OutsideWindow(incoming, RepositoryHistoryGuard.Read(document))
                && snapshot.ProtectedHistory?.Contains(DurableRepository.GetAddress(incoming.Scope, incoming.Key) + incoming.Version, StringComparer.Ordinal) != true)
            {
                continue;
            }
            string address = DurableRepository.GetAddress(incoming.Scope, incoming.Key);
            if (document.History?.RetiredKeys.Contains(address) == true)
            {
                continue;
            }
            document.Entries.TryGetValue(address, out DurableEntry? current);
            if (current is null || IsAncestor(document, current, incoming))
            {
                document.Entries[address] = incoming;
                document.Conflicts.RemoveAll(candidate => string.Equals(candidate.Scope, incoming.Scope, StringComparison.Ordinal) && string.Equals(candidate.Key, incoming.Key, StringComparison.Ordinal) && IsAncestor(document, candidate, incoming));
            }
            else
            {
                if (!IsAncestor(document, incoming, current) && !document.Conflicts.Contains(incoming))
                {
                    document.Conflicts.Add(incoming);
                }
            }
        }
        TrimConflictAncestors(document, snapshot.Entries);
        RepositoryEquivalentValues.Merge(document, snapshot.Entries);
        foreach (DurableReceipt receipt in snapshot.Receipts)
        {
            _ = ReceiptReconciliation.TryReconcile(document, receipt);
        }
        foreach (Guid operationId in document.Prepared.Keys.ToArray())
        {
            if (document.Receipts.TryGetValue(operationId, out DurableReceipt? receipt))
            {
                _ = ReleaseConfirmedReservation(document, receipt);
            }
            ReleaseRetiredReservation(document, operationId);
        }
        RepositoryCausality.Trim(document, snapshot.Entries);
        if (snapshot.HistoryBoundary is not null && !string.Equals(previousState, RepositoryStateVersion.HashEntries(document.Entries.Values.Concat(document.Conflicts)), StringComparison.Ordinal))
        {
            RepositoryCheckpointRequest.Set(document, true);
        }
        return document.Conflicts.Count == 0;
    }

    /// <summary>現在欄の候補とは並行でも、別の確認済み候補に包含された古い分岐は競合集合から外します。</summary>
    private static void TrimConflictAncestors(RepositoryDocument document, IReadOnlyList<DurableEntry> changed)
    {
        HashSet<(string Scope, string Key)> keys = changed.Select(entry => (entry.Scope, entry.Key)).ToHashSet();
        foreach (IGrouping<(string Scope, string Key), DurableEntry> group in document.Conflicts.GroupBy(entry => (entry.Scope, entry.Key)).Where(group => keys.Contains(group.Key)).ToArray())
        {
            string address = DurableRepository.GetAddress(group.Key.Scope, group.Key.Key);
            DurableEntry current = document.Entries[address];
            DurableEntry[] candidates = new[] { current }.Concat(group).DistinctBy(entry => ContractJson.Hash(entry)).ToArray();
            DurableEntry[] heads = candidates.Where(candidate => !candidates.Any(other => !string.Equals(candidate.Version, other.Version, StringComparison.Ordinal)
                && IsAncestor(document, candidate, other))).ToArray();
            if (heads.Length == 0)
            {
                throw new InvalidDataException("競合候補の祖先関係が循環しています。");
            }
            if (!heads.Contains(current))
            {
                current = heads.OrderBy(entry => entry.Version, StringComparer.Ordinal).First();
                document.Entries[address] = current;
            }
            document.Conflicts.RemoveAll(entry => string.Equals(entry.Scope, group.Key.Scope, StringComparison.Ordinal) && string.Equals(entry.Key, group.Key.Key, StringComparison.Ordinal));
            document.Conflicts.AddRange(heads.Where(entry => entry != current));
        }
    }

    /// <summary>古い予約に残る元本文と、圧縮前の確定結果・現在の祖先情報を併せて検証します。</summary>
    private static void ReleaseRetiredReservation(RepositoryDocument document, Guid id)
    {
        if (!document.Prepared.TryGetValue(id, out PreparedBatch? prepared))
        {
            return;
        }
        RetiredOperation? retired = document.History?.RetiredOperations.GetValueOrDefault(id);
        if (retired is not null && !string.Equals(prepared.Fingerprint, retired.Fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        bool expired = retired is null && RepositoryHistoryGuard.Expired(id, RepositoryHistoryGuard.Read(document)) is not null
            && prepared.Mutations.All(mutation => mutation.CausalVersion is not null);
        string aborted = "Persistence.Aborted";
        if (retired?.Outcome == EOutcome.Failed && string.Equals(retired.Reason, aborted, StringComparison.Ordinal))
        {
            document.Prepared.Remove(id);
            return;
        }
        if (retired?.Outcome != EOutcome.Succeeded && !expired)
        {
            return;
        }
        foreach (DurableMutation mutation in prepared.Mutations)
        {
            if (mutation.CausalVersion is not null)
            {
                RepositoryCausality.Remember(document, new(mutation.Scope, mutation.Key, id.ToString("N"), mutation.CausalVersion));
            }
        }
        DurableReceipt confirmed = new(id, prepared.Fingerprint, EOutcome.Succeeded, prepared.Mutations.Select(mutation => CreateResolved(id, mutation)).ToArray());
        _ = ReleaseConfirmedReservation(document, confirmed);
    }

    /// <summary>同期で先取込みした確定結果を検証し、元予約だけを解放します。</summary>
    /// <returns>予約なし、または同じ内容の確定を確認して解放できたかです。</returns>
    internal static bool ReleaseConfirmedReservation(RepositoryDocument document, DurableReceipt receipt)
    {
        if (!document.Prepared.TryGetValue(receipt.OperationId, out PreparedBatch? prepared))
        {
            return true;
        }
        if (!string.Equals(prepared.Fingerprint, receipt.Fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        string aborted = "Persistence.Aborted";
        if (receipt.Outcome == EOutcome.Failed && receipt.Entries.Count == 0 && string.Equals(receipt.Reason, aborted, StringComparison.Ordinal))
        {
            document.Prepared.Remove(receipt.OperationId);
            return true;
        }
        if (receipt.Outcome != EOutcome.Succeeded || receipt.Entries.Count != prepared.Mutations.Count)
        {
            return false;
        }
        foreach (DurableMutation mutation in prepared.Mutations)
        {
            DurableEntry expected = CreateResolved(receipt.OperationId, mutation);
            string expectedHash = ContractJson.Hash(expected);
            if (receipt.Entries.Count(entry => string.Equals(ContractJson.Hash(entry), expectedHash, StringComparison.Ordinal)) != 1)
            {
                return false;
            }
            DurableEntry? actual = document.Entries.GetValueOrDefault(DurableRepository.GetAddress(mutation.Scope, mutation.Key));
            bool present = actual is not null && IsAncestor(document, expected, actual);
            if (!present && !document.Conflicts.Any(entry => string.Equals(ContractJson.Hash(entry), expectedHash, StringComparison.Ordinal)))
            {
                return false;
            }
        }
        document.Prepared.Remove(receipt.OperationId);
        return true;
    }

    /// <summary>既存操作記録と残留予約が食い違う場合に元内容の成功を偽装しません。</summary>
    /// <returns>確認済み結果または予約不整合です。</returns>
    internal static DurableReceipt ConfirmReservedReceipt(RepositoryDocument document, DurableReceipt receipt)
    {
        if (ReleaseConfirmedReservation(document, receipt))
        {
            return receipt;
        }
        PreparedBatch prepared = document.Prepared[receipt.OperationId];
        if (!string.Equals(prepared.Fingerprint, receipt.Fingerprint, StringComparison.Ordinal))
        {
            return new(receipt.OperationId, prepared.Fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch");
        }
        return new(receipt.OperationId, prepared.Fingerprint, EOutcome.Unknown, [], "Persistence.ReservationUnconfirmed");
    }

    /// <summary>同一項目の親版を遡り、勝手な時刻順上書きを避けます。</summary>
    /// <returns>祖先または同一版か。</returns>
    internal static bool IsAncestor(RepositoryDocument document, DurableEntry ancestor, DurableEntry descendant)
    {
        if (string.Equals(ancestor.Version, descendant.Version, StringComparison.Ordinal))
        {
            return string.Equals(ContractJson.Hash(ancestor.Value), ContractJson.Hash(descendant.Value), StringComparison.Ordinal);
        }
        bool? causal = RepositoryCausality.IsAncestor(document, ancestor, descendant);
        if (causal.HasValue)
        {
            return causal.Value;
        }
        if (RepositoryLegacyAncestry.IsAncestor(document, ancestor, descendant))
        {
            return true;
        }
        HashSet<string> visited = new(StringComparer.Ordinal);
        Queue<DurableEntry> pending = new([descendant]);
        while (pending.TryDequeue(out DurableEntry? cursor))
        {
            if (!visited.Add(cursor.Version))
            {
                continue;
            }
            if (string.Equals(ancestor.Version, cursor.Version, StringComparison.Ordinal))
            {
                return string.Equals(ContractJson.Hash(ancestor.Value), ContractJson.Hash(cursor.Value), StringComparison.Ordinal);
            }
            string[] parents = (cursor.MergedVersions ?? []).Append(cursor.ParentVersion).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            DurableEntry[] known = document.Receipts.Values.SelectMany(receipt => receipt.Entries).Where(entry => string.Equals(entry.Scope, ancestor.Scope, StringComparison.Ordinal)
                && string.Equals(entry.Key, ancestor.Key, StringComparison.Ordinal) && parents.Contains(entry.Version, StringComparer.Ordinal)).ToArray();
            string[] missing = parents.Except(known.Select(entry => entry.Version), StringComparer.Ordinal).ToArray();
            IEnumerable<DurableEntry> restored = [];
            if (missing.Length != 0 && document.ReadAncestors is not null)
            {
                restored = document.ReadAncestors(ancestor.Scope, ancestor.Key, missing);
            }
            foreach (DurableEntry parent in known.Concat(restored))
            {
                pending.Enqueue(parent);
            }
        }
        return false;
    }

    /// <summary>解消操作の期待集合を検証します。</summary>
    /// <returns>確定または競合結果。</returns>
    public static DurableReceipt Resolve(RepositoryDocument document, Guid operationId, string scope, string key, IReadOnlyList<string> expectedVersions, NodeValue? value)
    {
        JsonHistoryAccess.Hydrate(document, operationId, []);
        string fingerprint = ContractJson.Hash(new { scope, key, expectedVersions, value });
        if (document.Receipts.TryGetValue(operationId, out DurableReceipt? previous))
        {
            if (string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return previous;
            }
            return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch");
        }
        DurableEntry? current = document.Entries.GetValueOrDefault(DurableRepository.GetAddress(scope, key));
        DurableEntry[] candidates = document.Conflicts.Where(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, key, StringComparison.Ordinal)).ToArray();
        HashSet<string> versions = new(candidates.Select(entry => entry.Version), StringComparer.Ordinal);
        if (current is not null)
        {
            versions.Add(current.Version);
        }
        if (versions.Count == 0 || !versions.SetEquals(expectedVersions) || DurableRepository.IsReserved(document, new DurableMutation(scope, key, current?.Version, value)))
        {
            return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Persistence.ConflictSetChanged");
        }
        DurableMutation stamped = RepositoryCausality.Stamp(document, [new(scope, key, current?.Version, value, versions.ToArray())])[0];
        DurableEntry resolved = new(scope, key, operationId.ToString("N"), value?.Copy(), current?.Version, versions.ToArray());
        document.Entries[DurableRepository.GetAddress(scope, key)] = resolved;
        document.Conflicts.RemoveAll(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, key, StringComparison.Ordinal));
        if (stamped.CausalVersion is not null)
        {
            RepositoryCausality.Remember(document, new(scope, key, resolved.Version, stamped.CausalVersion));
            RepositoryCausality.Trim(document, [resolved]);
        }
        DurableReceipt receipt = new(operationId, fingerprint, EOutcome.Succeeded, [resolved]);
        document.Receipts.Add(operationId, receipt);
        return receipt;
    }
}
