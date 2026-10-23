using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>保存媒体に依存しない比較更新の意味を定義します。</summary>
public abstract class DurableRepository : IDurableRepository, ILocalSnapshotBatches, Replication.IPreparedOperations
{
    /// <summary>共有準備だけを巡回し、終端結果の一覧を作りません。</summary>
    /// <returns>前回位置より後の有限な操作IDです。</returns>
    ValueTask<IReadOnlyList<Guid>> Replication.IPreparedOperations.ReadPreparedIdsAsync(Guid after, int maximum, CancellationToken cancellationToken)
        => ValueTask.FromResult(Access(document => Replication.ReplicationRecoveryIds.Read(document, after, maximum, false, cancellationToken), false));

    /// <summary>新形式の未確認判断も含め、元の操作だけを順に復旧します。</summary>
    /// <returns>元操作を再実行しない有限な確認対象です。</returns>
    ValueTask<IReadOnlyList<Guid>> Replication.IPreparedOperations.ReadRecoveryIdsAsync(Guid after, int maximum, CancellationToken cancellationToken)
        => ValueTask.FromResult(Access(document => Replication.ReplicationRecoveryIds.Read(document, after, maximum, true, cancellationToken), false));

    /// <summary>過去の成功結果を使って削除済みの受付を再実行しないための照合です。</summary>
    /// <returns>再利用できない受付キーかです。</returns>
    public virtual ValueTask<bool> IsHistoryRetiredAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => RepositoryHistoryGuard.IsRetiredKey(document, scope, key), false));
    }

    /// <summary>書込み元の順序だけを原子的に予約し、同じ親版を全複製先へ渡します。</summary>
    /// <returns>利用者の変更本文を維持した因果情報付き候補です。</returns>
    public virtual ValueTask<IReadOnlyList<DurableMutation>> StampAsync(IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access<IReadOnlyList<DurableMutation>>(document => RepositoryCausality.Stamp(document, mutations), true));
    }

    /// <summary>保存済み項目と版を読み取ります。</summary>
    /// <returns>確認した保存結果です。</returns>
    public virtual ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string address = GetAddress(scope, key);
        return ValueTask.FromResult(Access(document => document.Entries.GetValueOrDefault(address) ?? JsonHistoryAccess.Read<DurableEntry>(document, "entry", address), false));
    }

    /// <summary>専用読取りのない媒体でも、通常の検証を通して値と競合を返します。</summary>
    /// <returns>削除履歴を含む項目と、その用途の競合候補です。</returns>
    public virtual ValueTask<(DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts)> ReadWithConflictsAsync(string scope, string key, CancellationToken cancellationToken = default)
        => RepositoryObservations.ReadWithConflictsAsync(this, scope, key, cancellationToken);

    /// <summary>元の操作を確定・中止せず、対象項目の保存予約だけを確認します。</summary>
    /// <returns>未完了の保存準備があるかです。</returns>
    public virtual ValueTask<bool> HasPendingWriteAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DurableMutation target = new(scope, key, null, null);
        return ValueTask.FromResult(Access(document => IsReserved(document, target), false));
    }

    /// <summary>一用途の削除履歴を含む項目を列挙します。</summary>
    /// <returns>確認した保存結果です。</returns>
    public virtual ValueTask<IReadOnlyList<DurableEntry>> ListAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DurableEntry> result = Access(document => JsonHistoryAccess.List(document, scope), false);
        return ValueTask.FromResult(result);
    }

    /// <summary>通常の検証済み読取りから、前回と異なる本文だけを観測します。</summary>
    /// <returns>削除履歴を含む対象集合と変更本文です。</returns>
    public virtual ValueTask<IReadOnlyList<DurableObservation>> ObserveAsync(string scope, IReadOnlyDictionary<string, string> known,
        IReadOnlySet<string>? keys = null, CancellationToken cancellationToken = default)
        => RepositoryObservations.ReadAsync(this, scope, known, keys, cancellationToken);

    /// <summary>同じ操作を再適用せず保存結果を確認します。</summary>
    /// <returns>確認した保存結果です。</returns>
    public virtual ValueTask<DurableReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => JsonHistoryAccess.Receipt(document, operationId), false));
    }

    /// <summary>全項目の期待版を検査し、一取引で更新します。</summary>
    /// <returns>確認した保存結果です。</returns>
    public virtual ValueTask<DurableReceipt> ApplyBatchAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty || mutations.Count == 0)
        {
            throw new ArgumentException("操作IDと一つ以上の変更が必要です。", nameof(operationId));
        }
        DurableMutation[] copy = DurableMutationOwnership.Copy(mutations);
        string fingerprint = RepositoryCausality.Fingerprint(copy);
        return ValueTask.FromResult(Access(document => Apply(document, operationId, copy, fingerprint), true));
    }

    /// <summary>保存文書を検証して取引内でアクセスします。</summary>
    /// <returns>取引の結果。</returns>
    private protected abstract T Access<T>(Func<RepositoryDocument, T> operation, bool write);

    /// <summary>ローカル診断の現在値だけを原子的に置換します。</summary>
    /// <returns>保存確認結果。</returns>
    public virtual ValueTask<EOutcome> WriteLocalSnapshotAsync(string scope, string key, NodeValue value, CancellationToken cancellationToken = default)
        => WriteSnapshotsAsync([new(scope, key, value)], cancellationToken);

    /// <summary>ローカルの複数診断を、文書媒体でも同じ原子的な置換として保存します。</summary>
    /// <returns>一度の保存で確定した結果です。</returns>
    public virtual ValueTask<EOutcome> WriteSnapshotsAsync(IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(LocalSnapshotBatches.Apply(snapshots, operation => Access(operation, true), cancellationToken));

    /// <summary>従来値と直接JSON保存に同じローカルスコープ制約を適用します。</summary>
    internal static void ValidateLocalSnapshotScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        string prefix = "peer:";
        if (!scope.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("局所診断snapshotはpeer範囲に限ります。", nameof(scope));
        }
    }

    /// <summary>変更対象を永続予約し、別操作の割込みを防ぎます。</summary>
    /// <returns>予約確認結果。</returns>
    public virtual ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentOutOfRangeException.ThrowIfZero(mutations.Count);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("操作IDが必要です。", nameof(operationId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        string fingerprint = RepositoryCausality.Fingerprint(mutations);
        return ValueTask.FromResult(Access(document => Prepare(document, operationId, mutations, fingerprint), true));
    }

    /// <summary>準備応答を失った元操作の内容だけを読み、予約を再作成しません。</summary>
    /// <returns>現在準備中の内容指紋です。</returns>
    public virtual ValueTask<string?> ReadPreparationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => document.Prepared.GetValueOrDefault(operationId)?.Fingerprint, false));
    }

    /// <summary>予約済み内容と結果を同一取引で確定します。</summary>
    /// <returns>確定結果。</returns>
    public virtual ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => Commit(document, operationId), true));
    }

    /// <summary>共有可能な用途だけを同期用に取得します。</summary>
    /// <returns>実値と履歴。</returns>
    public virtual ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => RepositoryExport.Create(document), false));
    }

    /// <summary>現在状態の確認では、過去の操作結果一覧を作成しません。</summary>
    /// <returns>履歴本文を含まない復旧対象です。</returns>
    public virtual ValueTask<RepositorySnapshot> ReadCurrentStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => RepositoryExport.Create(document, includeReceipts: false), false));
    }

    /// <summary>非競合を先行反映し、分岐候補を保存します。</summary>
    /// <returns>競合がないか。</returns>
    public virtual ValueTask<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => RepositoryMerge.Merge(document, snapshot), true));
    }

    /// <summary>予約を確認または新規作成します。</summary>
    /// <returns>予約結果。</returns>
    internal static DurableReceipt Prepare(RepositoryDocument document, Guid operationId, IReadOnlyList<DurableMutation> mutations, string fingerprint)
    {
        JsonHistoryAccess.Hydrate(document, operationId, mutations);
        if (mutations.Any(mutation => RepositoryHistoryGuard.IsRetiredKey(document, mutation.Scope, mutation.Key)))
        {
            return new(operationId, fingerprint, EOutcome.Unknown, [], RepositoryHistoryGuard.ExpiredReason);
        }
        if (!document.Receipts.ContainsKey(operationId) && !document.Prepared.ContainsKey(operationId)
            && RepositoryHistoryGuard.Expired(operationId, RepositoryHistoryGuard.Read(document), document.History?.RetiredOperations.GetValueOrDefault(operationId)) is { } expired)
        {
            return expired;
        }
        if (document.Receipts.TryGetValue(operationId, out DurableReceipt? completed))
        {
            if (string.Equals(completed.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return completed;
            }
            return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch");
        }
        if (document.Prepared.TryGetValue(operationId, out PreparedBatch? prepared))
        {
            if (string.Equals(prepared.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new DurableReceipt(operationId, fingerprint, EOutcome.Succeeded, []);
            }
            return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch");
        }
        foreach (DurableMutation mutation in mutations)
        {
            document.Entries.TryGetValue(GetAddress(mutation.Scope, mutation.Key), out DurableEntry? current);
            if (!RepositoryMerge.Matches(document, mutation, current) || IsReserved(document, mutation))
            {
                return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Persistence.VersionConflict");
            }
        }
        document.Prepared.Add(operationId, new PreparedBatch(fingerprint, mutations.ToArray(), operationId));
        return new DurableReceipt(operationId, fingerprint, EOutcome.Succeeded, []);
    }

    /// <summary>予約を消費して一度だけ更新します。</summary>
    /// <returns>更新結果。</returns>
    private protected static DurableReceipt Commit(RepositoryDocument document, Guid operationId)
        => PreparedCommit.Complete(document, operationId);

    /// <summary>予約中止を保存して後着コミットを拒否します。</summary>
    /// <returns>操作結果。</returns>
    public virtual ValueTask<DurableReceipt> AbortPreparedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => Abort(document, operationId), true));
    }

    /// <summary>確定済み結果を壊さず予約を中止します。</summary>
    /// <returns>元結果または中止結果。</returns>
    private protected static DurableReceipt Abort(RepositoryDocument document, Guid operationId)
    {
        JsonHistoryAccess.Hydrate(document, operationId, []);
        if (document.Receipts.TryGetValue(operationId, out DurableReceipt? completed))
        {
            return RepositoryMerge.ConfirmReservedReceipt(document, completed);
        }
        document.Prepared.Remove(operationId, out PreparedBatch? prepared);
        DurableReceipt aborted = new(operationId, prepared?.Fingerprint ?? string.Empty, EOutcome.Failed, [], "Persistence.Aborted");
        document.Receipts.Add(operationId, aborted);
        return aborted;
    }

    /// <summary>同じ項目を他操作が予約しているか調べます。</summary>
    /// <returns>予約済みならtrue。</returns>
    internal static bool IsReserved(RepositoryDocument document, DurableMutation mutation)
    {
        return document.Prepared.Values.Any(batch => batch.Mutations.Any(item => string.Equals(item.Scope, mutation.Scope, StringComparison.Ordinal) && string.Equals(item.Key, mutation.Key, StringComparison.Ordinal)));
    }

    /// <summary>ローカル操作記録やPeer状態を他Peerの状態へ混入しません。</summary>
    /// <returns>共有対象か。</returns>
    internal static bool IsShared(string scope)
    {
        string peerPrefix = "peer:";
        string receiptPrefix = "receipts:";
        string clusterPrefix = "cluster:";
        return !scope.StartsWith(peerPrefix, StringComparison.Ordinal) && !scope.StartsWith(receiptPrefix, StringComparison.Ordinal) && !scope.StartsWith(clusterPrefix, StringComparison.Ordinal);
    }

    /// <summary>現在共有値だけから順序に依存しない識別子を作ります。</summary>
    /// <returns>内容識別子。</returns>
    public virtual ValueTask<string> GetSharedVersionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => RepositoryStateVersion.Hash(RepositoryExport.Create(document, includeReceipts: false)), false));
    }

    /// <summary>競合候補と現在値をまとめて提示します。</summary>
    /// <returns>明示解消の対象候補。</returns>
    public virtual ValueTask<IReadOnlyList<DurableEntry>> GetConflictsAsync(string scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<DurableEntry>>(Access(document => RepositoryObservations.ReadConflicts(document, scope), false));
    }

    /// <summary>人が確認した分岐集合だけを新しい一版へ統合します。</summary>
    /// <returns>明示解消の結果。</returns>
    public virtual ValueTask<DurableReceipt> ResolveConflictAsync(Guid operationId, string scope, string key, IReadOnlyList<string> expectedVersions, NodeValue? value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedVersions);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access(document => RepositoryMerge.Resolve(document, operationId, scope, key, expectedVersions, value), true));
    }

    /// <summary>一取引の比較と操作重複を検査します。</summary>
    /// <returns>変更結果です。</returns>
    internal static DurableReceipt Apply(RepositoryDocument document, Guid operationId, DurableMutation[] mutations, string fingerprint, bool completingPrepared = false)
    {
        if (!completingPrepared)
        {
            JsonHistoryAccess.Hydrate(document, operationId, mutations);
            if (mutations.Any(mutation => RepositoryHistoryGuard.IsRetiredKey(document, mutation.Scope, mutation.Key)))
            {
                return new(operationId, fingerprint, EOutcome.Unknown, [], RepositoryHistoryGuard.ExpiredReason);
            }
        }
        if (!completingPrepared && !document.Receipts.ContainsKey(operationId) && RepositoryHistoryGuard.Expired(operationId, RepositoryHistoryGuard.Read(document), document.History?.RetiredOperations.GetValueOrDefault(operationId)) is { } expired)
        {
            return expired;
        }
        if (document.Receipts.TryGetValue(operationId, out DurableReceipt? previous))
        {
            if (string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return previous;
            }
            return new DurableReceipt(operationId, fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch");
        }
        HashSet<string> addresses = new(StringComparer.Ordinal);
        foreach (DurableMutation mutation in mutations)
        {
            string address = GetAddress(mutation.Scope, mutation.Key);
            document.Entries.TryGetValue(address, out DurableEntry? current);
            if (!addresses.Add(address) || !RepositoryMerge.Matches(document, mutation, current) || IsReserved(document, mutation))
            {
                DurableReceipt conflict = new(operationId, fingerprint, EOutcome.Failed, [], "Persistence.VersionConflict");
                document.Receipts.Add(operationId, conflict);
                return conflict;
            }
        }
        List<DurableEntry> applied = [];
        foreach (DurableMutation mutation in RepositoryCausality.Stamp(document, mutations))
        {
            // 同じ操作は全Peerで同じ版になるため、再同期でも別の変更に見せません。
            DurableEntry entry = RepositoryMerge.CreateResolved(operationId, mutation);
            document.Entries[GetAddress(mutation.Scope, mutation.Key)] = entry;
            if (mutation.CausalVersion is not null && IsShared(mutation.Scope))
            {
                RepositoryCausality.Remember(document, new(entry.Scope, entry.Key, entry.Version, mutation.CausalVersion));
            }
            if (mutation.ResolveVersions is not null)
            {
                document.Conflicts.RemoveAll(candidate => string.Equals(candidate.Scope, mutation.Scope, StringComparison.Ordinal) && string.Equals(candidate.Key, mutation.Key, StringComparison.Ordinal));
            }
            applied.Add(entry);
        }
        RepositoryCausality.Trim(document, applied);
        DurableReceipt receipt = new(operationId, fingerprint, EOutcome.Succeeded, applied);
        document.Receipts.Add(operationId, receipt);
        return receipt;
    }

    /// <summary>用途と識別子を曖昧さなく結合します。</summary>
    /// <returns>辞書内アドレス。</returns>
    internal static string GetAddress(string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return JsonSerializer.Serialize(new[] { scope, key });
    }
}
