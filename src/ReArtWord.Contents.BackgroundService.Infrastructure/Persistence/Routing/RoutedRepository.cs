using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>用途選択と元操作の固定先を、全読み書き・複製で一貫して適用します。</summary>
internal sealed class RoutedRepository(string directory, RepositoryRoutes routes, RoutingState state, RoutingSynchronization synchronization, SqliteConnectionLifetime? lifetime = null) : IDurableRepository, ILocalJsonSnapshots, IRequestReceiptStore, ILocalSnapshotBatches, IReplicationTransactionStore, ISnapshotExportSource, State.IDurableStateChanges, IPreparedOperations, ISharedChangeStamp
{
    private readonly RequestReceiptBatch receipts = new();

    /// <summary>実際に共有値を持つ全媒体の変更位置だけを取得します。</summary>
    /// <returns>未対応の媒体が混在する場合はnullです。</returns>
    ValueTask<string?> ISharedChangeStamp.ReadSharedChangeStampAsync(CancellationToken cancellationToken)
        => new(AccessAsync(() => RoutedReplication.ReadStampAsync(routes, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>共有保存先の準備だけを取込み排他の内側で確認します。</summary>
    /// <returns>元の媒体を変更しない、有限な巡回対象です。</returns>
    ValueTask<IReadOnlyList<Guid>> IPreparedOperations.ReadPreparedIdsAsync(Guid after, int maximum, CancellationToken cancellationToken)
        => new(AccessAsync(() => RoutedReplication.ReadIdsAsync(routes, after, maximum, false, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>局所の確定判断を持つ既定媒体も復旧巡回へ含めます。</summary>
    /// <returns>元の保存先を維持した確認対象です。</returns>
    ValueTask<IReadOnlyList<Guid>> IPreparedOperations.ReadRecoveryIdsAsync(Guid after, int maximum, CancellationToken cancellationToken)
        => new(AccessAsync(() => RoutedReplication.ReadIdsAsync(routes, after, maximum, true, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>状態の実保存先に購読を固定します。</summary>
    /// <returns>保存先の変更キューです。</returns>
    public State.DurableStateSubscription SubscribeState(string scope, string key) => ((State.IDurableStateChanges)routes.Stores[routes.Backend(scope)]).SubscribeState(scope, key);

    /// <summary>通常と同じ取込み排他と所有確認の中で、複製開始の一括保存を試みます。</summary>
    /// <returns>一括保存した結果、または書込みを行っていないnullです。</returns>
    ValueTask<ReplicationStartResult?> IReplicationTransactionStore.TryStartAsync(ReplicationStart start, CancellationToken cancellationToken)
        => new(AccessAsync(() => RoutedReplication.TryAsync(routes, start,
            (id, fingerprint, scopes, token) => ClaimAsync(id, fingerprint, scopes, true, token), cancellationToken), cancellationToken: cancellationToken));

    /// <summary>元の保存所有先を確認し、同一媒体なら判断とコミットを一括保存します。</summary>
    /// <returns>原子的な局所結果、または別媒体のため未変更のnullです。</returns>
    ValueTask<DurableReceipt?> IReplicationTransactionStore.TryCommitAsync(ReplicationCommit commit, CancellationToken cancellationToken)
        => new(AccessAsync(() => RoutedReplication.TryCommitAsync(routes, state, commit, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>受付の用途と同じ保存先で削除済みキーを確認します。</summary>
    /// <returns>再利用できない履歴の受付かです。</returns>
    public ValueTask<bool> IsHistoryRetiredAsync(string scope, string key, CancellationToken cancellationToken = default)
        => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].IsHistoryRetiredAsync(scope, key, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>業務変更と同じ単一媒体で、複製用の因果情報を固定します。</summary>
    /// <returns>媒体を跨ぐ不正な変更はそのまま通常の拒否判定へ渡します。</returns>
    public ValueTask<IReadOnlyList<DurableMutation>> StampAsync(IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
        => new(AccessAsync(async () =>
        {
            EStorageBackend? backend = routes.Select(mutations.Select(mutation => mutation.Scope));
            if (backend is null)
            {
                return mutations;
            }
            return await routes.Stores[backend.Value].StampAsync(mutations, cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken));

    /// <summary>用途割当で実際に使用するSQLiteだけを保守対象にします。</summary>
    internal SqliteRepository? HistoryStore => routes.Stores.Values.OfType<SqliteRepository>().SingleOrDefault();

    /// <summary>JSONへ割り当てた共有用途も同じ保持設定の対象です。</summary>
    internal Json.JsonFileRepository? JsonHistoryStore => routes.Stores.Values.OfType<Json.JsonFileRepository>().SingleOrDefault();

    /// <summary>選別と確定だけを既存の取込み排他へ接続し、圧縮計算中には排他を保持しません。</summary>
    /// <returns>同じ保存領域を所有して行った短い処理の結果です。</returns>
    internal Task<T> HistoryAccessAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
        => AccessAsync(() => Task.FromResult(operation()), cancellationToken: cancellationToken);

    /// <summary>構成時に固定した受付スコープの媒体へ、取込み排他を維持してCASを渡します。</summary>
    /// <returns>要求自体の保存結果です。重複したrouting操作履歴は作りません。</returns>
    ValueTask<DurableEntry?> IRequestReceiptStore.CompareExchangeReceiptAsync(string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken)
        => receipts.WriteAsync(new(scope, key, expectedVersion, value), WriteReceiptsAsync, cancellationToken);

    /// <summary>媒体を固定した個別CASを、取込み排他と一つのSQLite取引の中で確認します。</summary>
    /// <returns>入力順の耐久確認結果です。</returns>
    private ValueTask<IReadOnlyList<DurableEntry?>> WriteReceiptsAsync(Func<IReadOnlyList<ReceiptWrite>> take, CancellationToken cancellationToken = default)
        => new(AccessAsync(async () =>
        {
            IReadOnlyList<ReceiptWrite> writes = take();
            DurableEntry?[] results = new DurableEntry?[writes.Count];
            foreach (IGrouping<EStorageBackend, int> group in Enumerable.Range(0, writes.Count).GroupBy(index => routes.Backend(writes[index].Scope)))
            {
                IDurableRepository store = routes.Stores[group.Key];
                int[] indices = group.ToArray();
                if (store is SqliteRepository sqlite)
                {
                    IReadOnlyList<DurableEntry?> saved = sqlite.CompareExchangeReceipts(indices.Select(index => writes[index]).ToArray(), cancellationToken);
                    for (int index = 0; index < indices.Length; index++)
                    {
                        results[indices[index]] = saved[index];
                    }
                }
                else
                {
                    foreach (int index in indices)
                    {
                        ReceiptWrite write = writes[index];
                        results[index] = await RequestReceiptPersistence.WriteAsync(store, write.Scope, write.Key, write.ExpectedVersion, write.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            return (IReadOnlyList<DurableEntry?>)results;
        }, cancellationToken: cancellationToken));

    /// <summary>指定スコープの実保存先から読みます。</summary>
    /// <returns>元の版と値です。</returns>
    public ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].ReadAsync(scope, key, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>取込み途中の確認と排他を一回だけ行い、同じ媒体の値と競合を取得します。</summary>
    /// <returns>用途に対応する保存先の項目と競合です。</returns>
    public ValueTask<(DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts)> ReadWithConflictsAsync(string scope, string key, CancellationToken cancellationToken = default)
        => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].ReadWithConflictsAsync(scope, key, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>指定用途の保存先で、対象項目を保持する未完了予約を確認します。</summary>
    /// <returns>保存準備が残っているかです。</returns>
    public ValueTask<bool> HasPendingWriteAsync(string scope, string key, CancellationToken cancellationToken = default)
        => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].HasPendingWriteAsync(scope, key, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>同じスコープの保存値を列挙します。</summary>
    /// <returns>削除履歴を含む項目です。</returns>
    public ValueTask<IReadOnlyList<DurableEntry>> ListAsync(string scope, CancellationToken cancellationToken = default) => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].ListAsync(scope, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>用途の実保存先で整合性を検査し、取込みの途中値を観測しません。</summary>
    /// <returns>対象集合と変更本文です。</returns>
    public ValueTask<IReadOnlyList<DurableObservation>> ObserveAsync(string scope, IReadOnlyDictionary<string, string> known,
        IReadOnlySet<string>? keys = null, CancellationToken cancellationToken = default)
        => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].ObserveAsync(scope, known, keys, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>跨媒体の変更を一つの取引と偽らず、開始前に拒否します。</summary>
    /// <returns>元操作の確定結果です。</returns>
    public ValueTask<DurableReceipt> ApplyBatchAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default) => ChangeAsync(operationId, mutations, false, cancellationToken);

    /// <summary>予約先を永続固定してから既存媒体へ準備を依頼します。</summary>
    /// <returns>予約または拒否の結果です。</returns>
    public ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default) => ChangeAsync(operationId, mutations, true, cancellationToken);

    /// <summary>元の媒体だけへコミットを渡します。</summary>
    /// <returns>元の保存操作結果です。</returns>
    public ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default) => new(AccessAsync(() => CompleteAsync(operationId, false, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>元の媒体だけへ予約中止を渡します。</summary>
    /// <returns>確定済みなら元の確定結果です。</returns>
    public ValueTask<DurableReceipt> AbortPreparedAsync(Guid operationId, CancellationToken cancellationToken = default) => new(AccessAsync(() => CompleteAsync(operationId, true, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>空結果であっても元の媒体から操作記録を返します。</summary>
    /// <returns>元操作の保存結果です。</returns>
    public ValueTask<DurableReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) => new(AccessAsync(() => state.ReceiptAsync(operationId, routes, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>準備を受け付けた元の媒体だけを照会し、別媒体へ作り直しません。</summary>
    /// <returns>確定済みや中止済みを含めず、準備中だけの指紋です。</returns>
    public ValueTask<string?> ReadPreparationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => new(AccessAsync(() => RoutedReplication.ReadPreparationAsync(routes, state, operationId, cancellationToken), cancellationToken: cancellationToken));

    /// <summary>ローカル情報を除き全媒体の現在候補と履歴を取得します。</summary>
    /// <returns>操作の論理範囲を含む固定スナップショットです。</returns>
    public ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default) => new(AccessAsync(() => synchronization.ExportAsync(cancellationToken), cancellationToken: cancellationToken));

    /// <summary>取込み排他を維持し、過去の確定結果を含めず共有現在値を確認します。</summary>
    /// <returns>媒体別の現在値・競合・保持境界・未確定予約です。</returns>
    public ValueTask<RepositorySnapshot> ReadCurrentStateAsync(CancellationToken cancellationToken = default) => new(AccessAsync(() => synchronization.ReadCurrentAsync(cancellationToken), cancellationToken: cancellationToken));

    /// <summary>論理所有先と未完了Importの排他を維持して、共有履歴を一件ずつ書き出します。</summary>
    /// <returns>固定した版を使用した書出し結果です。</returns>
    Task<T> ISnapshotExportSource.ReadExportAsync<T>(Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken)
        => AccessAsync(() => new RoutingSnapshotExport(routes, state).ReadAsync(read, cancellationToken), cancellationToken: cancellationToken);

    /// <summary>一つの共有媒体で同じ操作本文を再検証できる場合だけ、照合後の本文読取りを使用します。</summary>
    bool ISnapshotExportSource.SupportsDeferredReceiptReads => routes.SharedStores.Count == 1 && routes.SharedStores[0] is ISnapshotExportSource { SupportsDeferredReceiptReads: true };

    /// <summary>固定スナップショット全体の取込みが完了するまで途中値を公開しません。</summary>
    /// <returns>全媒体の取込みが完了し競合がないかです。</returns>
    public ValueTask<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default) => new(AccessAsync(() => synchronization.ImportAsync(snapshot, cancellationToken), true, cancellationToken: cancellationToken));

    /// <summary>バックエンド構成差に依存しない共通の共有内容版です。</summary>
    /// <returns>候補を含めた内容ハッシュです。</returns>
    public ValueTask<string> GetSharedVersionAsync(CancellationToken cancellationToken = default) => new(AccessAsync(() => synchronization.VersionAsync(cancellationToken), cancellationToken: cancellationToken));

    /// <summary>診断用途を指定した媒体へ現在値だけを保存します。</summary>
    /// <returns>保存確認結果です。</returns>
    public ValueTask<EOutcome> WriteLocalSnapshotAsync(string scope, string key, NodeValue value, CancellationToken cancellationToken = default) => new(AccessAsync(() => routes.Stores[routes.Backend(scope)].WriteLocalSnapshotAsync(scope, key, value, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>同じ保存媒体に属する診断だけを一度に確定します。</summary>
    /// <returns>媒体を跨ぐ場合は書込み前に拒否します。</returns>
    ValueTask<EOutcome> ILocalSnapshotBatches.WriteSnapshotsAsync(IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken)
        => new(AccessAsync(() =>
        {
            _ = LocalSnapshotBatches.Own(snapshots, cancellationToken);
            EStorageBackend backend = routes.Select(snapshots.Select(item => item.Scope)) ?? throw new ArgumentException("診断snapshotは同じ媒体へ保存してください。", nameof(snapshots));
            ILocalSnapshotBatches target = routes.Stores[backend] as ILocalSnapshotBatches ?? throw new NotSupportedException("保存媒体は原子的な診断snapshotに対応していません。");
            return target.WriteSnapshotsAsync(snapshots, cancellationToken).AsTask();
        }, cancellationToken: cancellationToken));

    /// <summary>直接JSON保存でも用途別の媒体と取込み排他を維持します。</summary>
    /// <returns>保存先が確認した結果です。</returns>
    ValueTask<EOutcome> ILocalJsonSnapshots.WriteJsonAsync(string scope, string key, Action<System.Text.Json.Utf8JsonWriter> writeValue, CancellationToken cancellationToken)
        => new(AccessAsync(() => LocalJsonSnapshots.WriteAsync(routes.Stores[routes.Backend(scope)], scope, key, writeValue, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>型付き復元でも保存先と未完了取込みの境界を維持します。</summary>
    /// <returns>用途の実保存先から復元した値です。</returns>
    ValueTask<T?> ILocalJsonSnapshots.ReadJsonAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class
        => new(AccessAsync(() => LocalJsonSnapshots.ReadAsync<T>(routes.Stores[routes.Backend(scope)], scope, key, cancellationToken).AsTask(), cancellationToken: cancellationToken));

    /// <summary>スコープ未指定では全媒体の競合候補を保持して返します。</summary>
    /// <returns>同一キーの複数versionを含む候補です。</returns>
    public ValueTask<IReadOnlyList<DurableEntry>> GetConflictsAsync(string scope, CancellationToken cancellationToken = default) => new(AccessAsync(async () =>
    {
        if (scope.Length != 0)
        {
            return await routes.Stores[routes.Backend(scope)].GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        List<DurableEntry> entries = [];
        foreach (IDurableRepository store in routes.SharedStores)
        {
            entries.AddRange(await store.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false));
        }
        return (IReadOnlyList<DurableEntry>)entries.ToArray();
    }, cancellationToken: cancellationToken));

    /// <summary>確認した候補集合の操作も元媒体へ固定します。</summary>
    /// <returns>元の指紋を保持した解消結果です。</returns>
    public ValueTask<DurableReceipt> ResolveConflictAsync(Guid operationId, string scope, string key, IReadOnlyList<string> expectedVersions, NodeValue? value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedVersions);
        string[] ownedVersions = expectedVersions.ToArray();
        NodeValue? ownedValue = value?.Copy();
        string fingerprint = ContractJson.Hash(new { scope, key, expectedVersions = ownedVersions, value = ownedValue });
        return new(AccessAsync(async () =>
        {
            RoutingOwner owner = await ClaimAsync(operationId, fingerprint, [scope], false, cancellationToken).ConfigureAwait(false);
            if (owner.Result is not null)
            {
                return owner.Result;
            }
            return await routes.Stores[owner.Backend].ResolveConflictAsync(operationId, scope, key, ownedVersions, ownedValue, cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken));
    }

    /// <summary>呼出し元の可変値を所有してから媒体の取引へ渡します。</summary>
    /// <returns>比較保存または予約結果です。</returns>
    private ValueTask<DurableReceipt> ChangeAsync(Guid id, IReadOnlyList<DurableMutation> mutations, bool prepare, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentOutOfRangeException.ThrowIfZero(mutations.Count);
        DurableMutation[] owned = DurableMutationOwnership.Copy(mutations);
        string fingerprint = RepositoryCausality.Fingerprint(owned);
        return new(AccessAsync(async () =>
        {
            string[] scopes = owned.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            bool atomic = routes.Select(scopes) == routes.Default && routes.Stores[routes.Default] is SqliteRepository;
            RoutingOwner owner = await ClaimAsync(id, fingerprint, scopes, atomic, cancellationToken).ConfigureAwait(false);
            if (owner.Result is not null)
            {
                return owner.Result;
            }
            IDurableRepository store = routes.Stores[owner.Backend];
            if (atomic && store is SqliteRepository sqlite)
            {
                return sqlite.ChangeWithOwner(owner, owned, prepare, cancellationToken);
            }
            if (prepare)
            {
                return await store.PrepareAsync(id, owned, cancellationToken).ConfigureAwait(false);
            }
            return await store.ApplyBatchAsync(id, owned, cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken));
    }

    /// <summary>global operationIdの指紋と保存先を永続固定します。</summary>
    /// <returns>不一致や跨媒体は確定した拒否を含みます。</returns>
    private async Task<RoutingOwner> ClaimAsync(Guid id, string fingerprint, IReadOnlyList<string> scopes, bool deferSave, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("操作IDが必要です。", nameof(id));
        }
        RoutingOwner? old = await state.OwnerAsync(id, cancellationToken).ConfigureAwait(false);
        if (old is not null)
        {
            if (!string.Equals(old.Operation.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return old with { Result = new(id, fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch") };
            }
            return old;
        }
        DurableReceipt? receipt = await state.ReceiptAsync(id, routes, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                receipt = new(id, fingerprint, EOutcome.Failed, [], "Persistence.OperationMismatch");
            }
            return new(new(id, fingerprint, scopes), routes.Default, receipt);
        }
        EStorageBackend? backend = routes.Select(scopes);
        DurableReceipt? rejected = null;
        if (!backend.HasValue)
        {
            rejected = new(id, fingerprint, EOutcome.NotExecuted, [], "Persistence.CrossBackendBatch");
        }
        RoutingOwner owner = new(new(id, fingerprint, scopes), backend ?? routes.Default, rejected);
        if (!deferSave)
        {
            await state.SaveOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        return owner;
    }

    /// <summary>prepareの元保存先を起動し直しても追跡します。</summary>
    /// <returns>未確認操作を別媒体へ差し替えない結果です。</returns>
    private async Task<DurableReceipt> CompleteAsync(Guid id, bool abort, CancellationToken cancellationToken = default)
    {
        RoutingOwner? owner = await state.OwnerAsync(id, cancellationToken).ConfigureAwait(false);
        DurableReceipt? receipt = await state.ReceiptAsync(id, routes, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            return receipt;
        }
        if (owner is null && routes.Stores.Count != 1 && !abort)
        {
            return new(id, string.Empty, EOutcome.Unknown, [], "Persistence.PreparationMissing");
        }
        if (owner is null)
        {
            owner = new(new(id, string.Empty, []), routes.Default);
            await state.SaveOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        if (abort)
        {
            return await routes.Stores[owner.Backend].AbortPreparedAsync(id, cancellationToken).ConfigureAwait(false);
        }
        DurableReceipt committed = await routes.Stores[owner.Backend].CommitAsync(id, cancellationToken).ConfigureAwait(false);
        if (owner.Operation.Scopes.Count == 0 && committed.Entries.Count != 0)
        {
            PersistenceOperationOwner migrated = new(id, committed.Fingerprint, committed.Entries.Select(entry => entry.Scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            await state.SaveOwnerAsync(new(migrated, owner.Backend), cancellationToken).ConfigureAwait(false);
        }
        return committed;
    }

    /// <summary>全取込み完了前の保存値を別処理へ公開しません。</summary>
    /// <returns>ローカル排他中に実行した処理結果です。</returns>
    private async Task<T> AccessAsync<T>(Func<Task<T>> operation, bool importing = false, CancellationToken cancellationToken = default)
    {
        using RoutingLease lease = await RoutingLease.AcquireAsync(Path.Combine(directory, "routing.lock"), cancellationToken).ConfigureAwait(false);
        using SqliteConnectionScope? connections = routes.Stores.ContainsKey(EStorageBackend.Sqlite) ? new(lifetime)
                                                                                                     : null;
        if (!importing && await state.PendingAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            // 通信中断後も同じ固定操作履歴を完了させ、通常アクセスだけが永久に閉じる状態を残しません。
            await synchronization.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        return await operation().ConfigureAwait(false);
    }
}
