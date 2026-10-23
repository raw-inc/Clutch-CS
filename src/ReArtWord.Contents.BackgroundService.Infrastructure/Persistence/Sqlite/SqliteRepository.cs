using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>頻繁なCASでは対象行だけを読み書きするSQLite保存です。</summary>
public sealed class SqliteRepository : DurableRepository, ILocalJsonSnapshots, IRequestReceiptStore, IReplicationTransactionStore, ISnapshotExportSource, State.IDurableStateChanges, IPreparedOperations, ISharedChangeStamp
{
    private readonly string path;
    private readonly SqliteCurrentVersion versions;
    private readonly SqlitePreparationCache preparations = new();
    private readonly HistoryObservationCache observations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> receiptHashes = new(StringComparer.Ordinal);

    /// <summary>履歴保守が同じ保存先と同じ取引規則を使用するための絶対パスです。</summary>
    internal string HistoryPath => path;

    /// <summary>本文検査と別に、短い待機を解除する変更世代だけを取得します。</summary>
    /// <returns>元DBの世代です。</returns>
    ValueTask<string?> ISharedChangeStamp.ReadSharedChangeStampAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(SqliteSharedChanges.Stamp(path, cancellationToken));

    /// <summary>共有準備の有無を過去本文や共有現在値から独立して確認します。</summary>
    /// <returns>巡回する有限な準備識別子です。</returns>
    ValueTask<IReadOnlyList<Guid>> IPreparedOperations.ReadPreparedIdsAsync(Guid after, int maximum, CancellationToken cancellationToken)
        => ValueTask.FromResult(SqlitePreparations.Read(path, after, maximum, preparations, cancellationToken));

    /// <summary>局所準備の終了後も、元参加者の確認を有限件数で継続します。</summary>
    /// <returns>元操作の復旧確認対象です。</returns>
    ValueTask<IReadOnlyList<Guid>> IPreparedOperations.ReadRecoveryIdsAsync(Guid after, int maximum, CancellationToken cancellationToken)
        => ValueTask.FromResult(SqlitePreparations.ReadRecovery(path, after, maximum, preparations, cancellationToken));

    /// <summary>この保存先の確定した状態変更だけを購読します。</summary>
    /// <returns>呼出し元が解放する購読です。</returns>
    public State.DurableStateSubscription SubscribeState(string scope, string key) => State.DurableStateNotifications.Subscribe(path, scope, key);

    /// <summary>送信前の開始記録と因果位置と準備を一度に耐久保存します。</summary>
    /// <returns>同じ取引の開始・準備結果です。</returns>
    ValueTask<ReplicationStartResult?> IReplicationTransactionStore.TryStartAsync(ReplicationStart start, CancellationToken cancellationToken)
        => ValueTask.FromResult<ReplicationStartResult?>(SqliteReplicationTransactions.Write(path, start, cancellationToken: cancellationToken));

    /// <summary>送信判断と元コミットを同じ耐久取引にします。</summary>
    /// <returns>確定を確認した局所結果です。</returns>
    ValueTask<DurableReceipt?> IReplicationTransactionStore.TryCommitAsync(ReplicationCommit commit, CancellationToken cancellationToken)
        => ValueTask.FromResult<DurableReceipt?>(SqliteReplicationTransactions.Commit(path, commit, cancellationToken));

    /// <summary>保持済み本文と削除索引を一つの読取り取引で照合します。</summary>
    /// <returns>削除済みの受付かです。</returns>
    public override ValueTask<bool> IsHistoryRetiredAsync(string scope, string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SqliteHistoryGuard.IsRetiredKey(path, scope, key, cancellationToken));

    /// <summary>明示保存先に必要な表を作成します。</summary>
    public SqliteRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        versions = new(this.path, preparedCache: preparations);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        using SqliteSession session = new(this.path, initialize: true);
        session.Commit();
    }

    /// <summary>同じ書込み取引で親版と書込み元の順序を固定します。</summary>
    /// <returns>全Peerへ同じ内容で送れる変更候補です。</returns>
    public override ValueTask<IReadOnlyList<DurableMutation>> StampAsync(IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        RepositoryDocument document = session.Load(Guid.Empty, mutations);
        IReadOnlyList<DurableMutation> result = RepositoryCausality.Stamp(document, mutations);
        session.Save(document);
        session.Commit();
        return ValueTask.FromResult(result);
    }

    /// <summary>受付自体を一行のCASで確定し、同じ値の操作履歴を重ねて作りません。</summary>
    /// <returns>確定した項目、または既存版・予約との不一致です。</returns>
    ValueTask<DurableEntry?> IRequestReceiptStore.CompareExchangeReceiptAsync(string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken)
        => ValueTask.FromResult(CompareExchangeReceipts([new(scope, key, expectedVersion, value)], cancellationToken)[0]);

    /// <summary>各キーの比較条件を保ち、同時受付の耐久flushだけを一取引へまとめます。</summary>
    /// <returns>全コミット後の個別結果。比較不一致はその項目だけnullです。</returns>
    internal IReadOnlyList<DurableEntry?> CompareExchangeReceipts(IReadOnlyList<ReceiptWrite> writes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (ReceiptWrite write in writes)
        {
            RequestReceiptPersistence.Validate(write.Scope, write.Key, write.Value);
        }
        using SqliteSession session = new(path);
        List<DurableEntry?> results = [];
        foreach (ReceiptWrite write in writes)
        {
            string address = GetAddress(write.Scope, write.Key);
            DurableEntry? previous = session.Read<DurableEntry>("entry", address);
            if (!string.Equals(previous?.Version, write.ExpectedVersion, StringComparison.Ordinal) || session.HasPendingWrite(write.Scope, write.Key)
                || previous is null && SqliteHistoryGuard.IsRetiredKey(session, write.Scope, write.Key))
            {
                results.Add(null);
                continue;
            }
            DurableEntry entry = new(write.Scope, write.Key, Guid.NewGuid().ToString("N"), write.Value.Copy(), write.ExpectedVersion);
            RepositoryDocument document = new();
            document.Entries[address] = entry;
            session.Save(document);
            results.Add(entry);
        }
        session.Commit();
        return results;
    }

    /// <summary>対象行だけを読みます。</summary>
    /// <returns>保存項目。</returns>
    public override ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReadRecord<DurableEntry>("entry", GetAddress(scope, key), cancellationToken));

    /// <summary>競合と現在値を同じ読取り取引で検証し、取引準備を繰り返しません。</summary>
    /// <returns>同じ時点の指定項目と競合候補です。</returns>
    public override ValueTask<(DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts)> ReadWithConflictsAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string address = GetAddress(scope, key);
        using SqliteSession session = new(path, write: false);
        IReadOnlyList<DurableEntry> conflicts = session.ReadConflicts(scope, cancellationToken);
        return ValueTask.FromResult((session.Read<DurableEntry>("entry", address), conflicts));
    }

    /// <summary>同期に必要な複数行を一つの読取り取引で検証します。</summary>
    /// <returns>実在する検証済みの項目です。</returns>
    internal ValueTask<IReadOnlyList<DurableEntry>> ReadEntriesAsync(string scope, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        string[] addresses = keys.Select(key => GetAddress(scope, key)).ToArray();
        Dictionary<string, DurableEntry> entries = session.List<DurableEntry>("entry", scope, keys: addresses, cancellationToken: cancellationToken).ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<DurableEntry>>(keys.Where(entries.ContainsKey).Select(key => entries[key]).ToArray());
    }

    /// <summary>対象項目の予約を索引で調べ、準備済み内容の整合性も確認します。</summary>
    /// <returns>全履歴を走査せず確認した予約の有無です。</returns>
    public override ValueTask<bool> HasPendingWriteAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return ValueTask.FromResult(session.HasPendingWrite(scope, key));
    }

    /// <summary>指定用途の項目を読みます。</summary>
    /// <returns>保存項目一覧。</returns>
    public override ValueTask<IReadOnlyList<DurableEntry>> ListAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        IReadOnlyList<DurableEntry> result = session.List<DurableEntry>("entry", scope, cancellationToken: cancellationToken);
        return ValueTask.FromResult(result);
    }

    /// <summary>同じ取引で本文を検証し、変更がある項目だけを復号します。</summary>
    /// <returns>現在の対象集合と変更本文です。</returns>
    public override ValueTask<IReadOnlyList<DurableObservation>> ObserveAsync(string scope, IReadOnlyDictionary<string, string> known,
        IReadOnlySet<string>? keys = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SqliteObservations.Read(path, scope, known, keys, observations, cancellationToken));

    /// <summary>一操作の操作記録だけを読みます。</summary>
    /// <returns>保存結果。</returns>
    public override ValueTask<DurableReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReadRecord<DurableReceipt>("receipt", operationId.ToString("N"), cancellationToken));

    /// <summary>同期で個別確認が必要な操作結果を一取引で読み、圧縮展開を操作件数分繰り返しません。</summary>
    /// <returns>保存済み・失効済みの結果です。未保存の操作は含めません。</returns>
    internal ValueTask<IReadOnlyDictionary<Guid, DurableReceipt>> ReadReceiptsAsync(IReadOnlyCollection<Guid> operationIds, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SqliteReceipts.ReadStored(path, operationIds, cancellationToken));

    /// <summary>同じ操作の準備行だけを検証し、変更や新しい受付を作りません。</summary>
    /// <returns>現在準備中の内容指紋です。</returns>
    public override ValueTask<string?> ReadPreparationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(preparations.Read(path, operationId, cancellationToken));

    /// <summary>一行の取得で取消確認と読取り取引の寿命を統一します。</summary>
    /// <returns>破損検証を終えた元の保存値です。</returns>
    private T? ReadRecord<T>(string kind, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return session.Read<T>(kind, key);
    }

    /// <summary>ハートビートで過去操作記録を全走査しません。</summary>
    /// <returns>現在値の内容識別子。</returns>
    public override ValueTask<string> GetSharedVersionAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(versions.Read(cancellationToken));

    /// <summary>同期対象外のローカルデータを読み込まず、同じ共有スナップショットを返します。</summary>
    /// <returns>共有値と共有可能な履歴です。</returns>
    public override ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SqliteSnapshot.Read(path, true, cancellationToken));

    /// <summary>過去の操作結果本文を触らず、現在状態を同じ読取り取引で検証します。</summary>
    /// <returns>現在値・競合・復旧境界・未確定予約です。</returns>
    public override ValueTask<RepositorySnapshot> ReadCurrentStateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(SqliteSnapshot.Read(path, false, cancellationToken));

    /// <summary>履歴本文を保持せず、現在値と同じ読取り取引から必要な一件だけを復元します。</summary>
    /// <returns>固定版の書出し結果です。</returns>
    Task<T> ISnapshotExportSource.ReadExportAsync<T>(Func<SnapshotExport, Task<T>> read, CancellationToken cancellationToken)
        => SqliteReceiptExport.ReadAsync(path, receiptHashes, read, cancellationToken);

    /// <summary>保存済み操作の指紋と本文を、必要時に個別照合できます。</summary>
    bool ISnapshotExportSource.SupportsDeferredReceiptReads => true;

    /// <summary>対象現在値・必要履歴・予約だけを既存mergeへ渡し、ローカル行を再生成しません。</summary>
    /// <returns>取込み後に競合がなければtrueです。</returns>
    public override ValueTask<bool> ImportAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        RepositoryDocument document = SqliteSnapshot.Import(session, snapshot, cancellationToken);
        bool result = RepositoryMerge.Merge(document, snapshot);
        session.Save(document);
        Recovery.RecoveryRecordLedger.RegisterSnapshot(session, snapshot, cancellationToken);
        SqliteHistoryRestore.PromoteProtected(session, snapshot, cancellationToken);
        session.Commit();
        return ValueTask.FromResult(result);
    }

    /// <summary>単一SQLite媒体では、取込内容と論理所有先を同じ耐久取引で確定します。</summary>
    /// <returns>取込み後に競合がなければtrueです。</returns>
    internal bool ImportRouted(RepositorySnapshot snapshot, IReadOnlyDictionary<Guid, Routing.RoutingOwner> existingOwners, Contracts.Persistence.EStorageBackend backend, CancellationToken cancellationToken)
        => SqliteRoutedImport.Apply(path, snapshot, existingOwners, backend, cancellationToken);

    /// <summary>用途振分け層が全ローカル操作を復号しないための、保存形式内の限定読取りです。</summary>
    /// <returns>共有すべき確定拒否結果です。</returns>
    internal ValueTask<IReadOnlyList<Routing.RoutingOwner>> ReadRoutingResultsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return ValueTask.FromResult(session.ReadRoutingResults());
    }

    /// <summary>同一同期に含まれる履歴を一取引で照合し、履歴件数分のDB再接続を防ぎます。</summary>
    /// <returns>チェックサムと実内容が一致した操作IDです。</returns>
    internal ValueTask<IReadOnlySet<Guid>> MatchingReceiptsAsync(IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return ValueTask.FromResult(session.MatchingReceipts(receipts, cancellationToken));
    }

    /// <summary>競合行と対応する現在行だけを読み、通常確認で全操作記録を走査しません。</summary>
    /// <returns>空スコープは全用途、それ以外は指定用途の候補です。</returns>
    public override ValueTask<IReadOnlyList<DurableEntry>> GetConflictsAsync(string scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return ValueTask.FromResult(session.ReadConflicts(scope, cancellationToken));
    }

    /// <summary>診断リングの対象一行だけを履歴なしで置換します。</summary>
    /// <returns>保存確認結果。</returns>
    public override ValueTask<EOutcome> WriteLocalSnapshotAsync(string scope, string key, NodeValue value, CancellationToken cancellationToken = default)
        => SqliteLocalSnapshots.WriteAsync(path, [new(scope, key, value)], cancellationToken);

    /// <summary>診断のヘッダーと詳細を同じ取引へまとめます。</summary>
    /// <returns>対象行だけの確定結果です。</returns>
    public override ValueTask<EOutcome> WriteSnapshotsAsync(IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken = default)
        => SqliteLocalSnapshots.WriteAsync(path, snapshots, cancellationToken);

    /// <summary>ローカルJSONを一取引の既存TEXT行へ直接保存します。</summary>
    /// <returns>永続確定の結果です。</returns>
    ValueTask<EOutcome> ILocalJsonSnapshots.WriteJsonAsync(string scope, string key, Action<System.Text.Json.Utf8JsonWriter> writeValue, CancellationToken cancellationToken)
    {
        ValidateLocalSnapshotScope(scope);
        return SqliteLocalSnapshots.WriteJsonAsync(path, scope, key, writeValue, cancellationToken);
    }

    /// <summary>起動時の診断復元でも大きい中間文書を保持しません。</summary>
    /// <returns>検証済みローカル値です。</returns>
    ValueTask<T?> ILocalJsonSnapshots.ReadJsonAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path, write: false);
        return ValueTask.FromResult(session.ReadJsonSnapshot<T>(scope, key, cancellationToken));
    }

    /// <summary>変更対象と競合予約だけを一取引で扱います。</summary>
    /// <returns>比較更新結果。</returns>
    public override ValueTask<DurableReceipt> ApplyBatchAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentOutOfRangeException.ThrowIfZero(mutations.Count);
        cancellationToken.ThrowIfCancellationRequested();
        DurableMutation[] copy = DurableMutationOwnership.Copy(mutations);
        return ValueTask.FromResult(Change(operationId, copy, document => Apply(document, operationId, copy, RepositoryCausality.Fingerprint(copy)), cancellationToken));
    }

    /// <summary>対象行の変更予約を作ります。</summary>
    /// <returns>予約結果。</returns>
    public override ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentOutOfRangeException.ThrowIfZero(mutations.Count);
        cancellationToken.ThrowIfCancellationRequested();
        DurableMutation[] copy = DurableMutationOwnership.Copy(mutations);
        return ValueTask.FromResult(Change(operationId, copy, document => Prepare(document, operationId, copy, RepositoryCausality.Fingerprint(copy)), cancellationToken));
    }

    /// <summary>同じSQLite内の操作所有先と変更を原子的に保存し、同じ操作で二度flushしません。</summary>
    /// <returns>既存と同じ比較・予約・重複照合の結果です。</returns>
    internal DurableReceipt ChangeWithOwner(Routing.RoutingOwner owner, DurableMutation[] mutations, bool prepare, CancellationToken cancellationToken = default)
        => SqliteRoutingOwnership.Change(path, owner, mutations, prepare, cancellationToken);

    /// <summary>予約した対象行だけを更新します。</summary>
    /// <returns>確定結果。</returns>
    public override ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Change(operationId, [], document => Commit(document, operationId), cancellationToken));

    /// <summary>予約を解放して後着コミットを拒否します。</summary>
    /// <returns>中止結果。</returns>
    public override ValueTask<DurableReceipt> AbortPreparedAsync(Guid operationId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Change(operationId, [], document => Abort(document, operationId), cancellationToken));

    /// <summary>同期時だけ全共有履歴へアクセスします。</summary>
    /// <returns>同期処理結果。</returns>
    private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
    {
        using SqliteSession session = new(path, write: write);
        RepositoryDocument document = session.LoadAll();
        T result = operation(document);
        if (write)
        {
            session.Save(document);
        }
        session.Commit();
        return result;
    }

    /// <summary>既存の比較更新規則へ必要行だけを渡します。</summary>
    /// <returns>同一取引の更新結果。</returns>
    private DurableReceipt Change(Guid operationId, DurableMutation[] mutations, Func<RepositoryDocument, DurableReceipt> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("操作IDが必要です。", nameof(operationId));
        }
        using SqliteSession session = new(path);
        RepositoryDocument document = session.Load(operationId, mutations);
        DurableReceipt result = operation(document);
        session.Save(document);
        session.Commit();
        return result;
    }
}
