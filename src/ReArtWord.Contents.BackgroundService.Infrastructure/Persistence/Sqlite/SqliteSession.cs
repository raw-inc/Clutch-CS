using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>項目・操作記録・予約を個別行で扱う一取引です。</summary>
internal sealed class SqliteSession : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly SqliteRecordAccess records;
    private readonly SqliteHistoryHeads historyHeads;
    private readonly Dictionary<string, string> loaded = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> preparedIds = [];
    private readonly HashSet<string> conflictKeys = new(StringComparer.Ordinal);
    private readonly bool trackChanges;
    private readonly string path;

    // 抑制理由: 読取りや診断取引には通知キューを作らず、Stateを書いた取引内だけで遅延生成します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "状態変更のない取引でコレクションを割り当てず、取引終了と同時に解放する遅延生成です。")]
    private Dictionary<string, DurableEntry>? stateChanges;

    /// <summary>更新だけ即時取引とし、通常読取りでDDLや永続マーカーの再書込みを行いません。</summary>
    public SqliteSession(string path, bool write = true, bool initialize = false, bool usePooling = true)
    {
        this.path = Path.GetFullPath(path);
        trackChanges = write;
        string markerPath = path + ".initialized";
        bool existed = File.Exists(path);
        if (!existed && File.Exists(markerPath))
        {
            throw new InvalidDataException("初期化済みSQLite保存先がありません。");
        }
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite;
        if (initialize)
        {
            mode = SqliteOpenMode.ReadWriteCreate;
        }
        connection = SqliteConnectionScope.Open(path, mode, usePooling);
        bool completed = false;
        try
        {
            using (SqliteCommand setup = connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
                if (initialize)
                {
                    if (!existed)
                    {
                        setup.CommandText += " PRAGMA auto_vacuum=INCREMENTAL;";
                    }
                    setup.CommandText += " CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL, key TEXT NOT NULL, scope TEXT, payload TEXT NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(kind,key)); CREATE INDEX IF NOT EXISTS scope_lookup ON records(kind,scope); CREATE TABLE IF NOT EXISTS reservations(address TEXT PRIMARY KEY, operation TEXT NOT NULL); CREATE TABLE IF NOT EXISTS repository_identity(version INTEGER NOT NULL);";
                    setup.CommandText += " CREATE INDEX IF NOT EXISTS shared_content_lookup ON records(kind,key,hash) WHERE " + SqliteSharedFingerprint.ScopeFilter + ";";
                }
                setup.ExecuteNonQuery();
            }
            transaction = connection.BeginTransaction(deferred: !write);
            using SqliteCommand identity = Create();
            identity.CommandText = "SELECT version FROM repository_identity";
            object? version = identity.ExecuteScalar();
            if (version is not null && (version is not long actualVersion || actualVersion is not (1 or 2 or 3 or 4)))
            {
                throw new InvalidDataException("この保存形式の版は扱えません。");
            }
            if (version is null)
            {
                if (existed || !initialize)
                {
                    throw new InvalidDataException("保存形式の識別情報がありません。");
                }
                identity.CommandText = "INSERT INTO repository_identity(version) VALUES(1)";
                identity.ExecuteNonQuery();
            }
            if (initialize)
            {
                SqliteReceipts.Initialize(connection, transaction);
                SqliteHistorySchema.Initialize(connection, transaction);
                SqliteRetentionSchema.Initialize(connection, transaction);
                SqliteSharedChanges.Initialize(connection, transaction);
                SqlitePreparations.Initialize(connection, transaction);
            }
            if (initialize && !File.Exists(markerPath))
            {
                using FileStream marker = new(markerPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
                marker.Flush(true);
            }
            records = new(connection, transaction, loaded, conflictKeys, version is long archiveVersion && 2 <= archiveVersion, write, version is 4L);
            historyHeads = new(connection, transaction, address => Read<DurableEntry>("entry", address));
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>一行を検査して読みます。</summary>
    /// <returns>指定行、未存在ならnull。</returns>
    public T? Read<T>(string kind, string key, bool remember = true) => records.Read<T>(kind, key, remember);

    /// <summary>実際に比較が必要な親版だけを読み、元の取引以外へ本文を保持しません。</summary>
    /// <returns>同じキーの、内容を検証済みの親候補です。</returns>
    internal IReadOnlyList<DurableEntry> ReadAncestors(string scope, string key, IReadOnlyCollection<string> versions, CancellationToken cancellationToken)
        => records.ReadAncestors(scope, key, versions, cancellationToken);

    /// <summary>同じ読取り取引に属する共有履歴の索引を作り、本文を取引外へ持ち出しません。</summary>
    /// <returns>一件ごとの固定参照です。</returns>
    internal IReadOnlyList<SnapshotReceipt> ExportReceipts(System.Collections.Concurrent.ConcurrentDictionary<string, string> hashes, CancellationToken cancellationToken)
        => SqliteReceiptExport.Read(connection, transaction, hashes, cancellationToken);

    /// <summary>用途の行だけを検査して取得します。</summary>
    /// <returns>一致する行。</returns>
    public T[] List<T>(string kind, string? scope = null, bool sharedOnly = false, bool excludeRetired = false, IReadOnlyCollection<string>? keys = null, CancellationToken cancellationToken = default) => records.List<T>(kind, scope, sharedOnly, excludeRetired, keys, cancellationToken);

    /// <summary>前回観測との比較も同じ読取り取引へ含めます。</summary>
    /// <returns>内容を検証済みの対象集合です。</returns>
    internal IReadOnlyList<DurableObservation> Observe(string scope, IReadOnlyDictionary<string, string> known, IReadOnlySet<string>? keys, HistoryObservationCache cache, CancellationToken cancellationToken)
        => records.Observe(scope, known, keys, cache, cancellationToken);

    /// <summary>現在取引で更新予約の所有先を検査します。</summary>
    /// <returns>対象に未完了の予約があるかです。</returns>
    internal bool HasPendingWrite(string scope, string key) => records.HasPendingWrite(scope, key);

    /// <summary>対象項目・同操作記録・競合予約だけを読みます。</summary>
    /// <returns>最小比較文書。</returns>
    public RepositoryDocument Load(Guid operationId, IReadOnlyList<DurableMutation> mutations)
    {
        RepositoryDocument document = new();
        string id = operationId.ToString("N");
        DurableReceipt? receipt = Read<DurableReceipt>("receipt", id);
        if (receipt is not null)
        {
            document.Receipts.Add(operationId, receipt);
        }
        PreparedBatch? own = Read<PreparedBatch>("prepared", id);
        if (own is not null)
        {
            document.Prepared.Add(operationId, own);
            preparedIds.Add(operationId);
        }
        IReadOnlyList<DurableMutation> targets = PreparedCommit.RequiredTargets(mutations, own, receipt);
        foreach (DurableMutation mutation in targets)
        {
            string address = DurableRepository.GetAddress(mutation.Scope, mutation.Key);
            DurableEntry? entry = Read<DurableEntry>("entry", address);
            if (entry is not null)
            {
                document.Entries[address] = entry;
            }
            if (entry is null && SqliteHistoryGuard.IsRetiredKey(this, mutation.Scope, mutation.Key))
            {
                (document.History ??= new()).RetiredKeys.Add(address);
            }
            using SqliteCommand reservation = Create();
            reservation.CommandText = "SELECT operation FROM reservations WHERE address=$address";
            reservation.Parameters.AddWithValue("$address", address);
            if (reservation.ExecuteScalar() is string otherId)
            {
                Guid other = Guid.Parse(otherId);
                PreparedBatch? pending = Read<PreparedBatch>("prepared", otherId);
                if (pending is not null)
                {
                    document.Prepared[other] = pending;
                    preparedIds.Add(other);
                }
            }
        }
        foreach (string scope in targets.Select(mutation => mutation.Scope).Distinct(StringComparer.Ordinal))
        {
            document.Conflicts.AddRange(List<DurableEntry>("conflict", scope));
        }
        SqliteCausality.Load(this, document, targets.Select(mutation => (mutation.Scope, mutation.Key)));
        return document;
    }

    /// <summary>全体同期に使う保存状態を読みます。</summary>
    /// <returns>現在の文書。</returns>
    public RepositoryDocument LoadAll()
    {
        RepositoryDocument document = new();
        foreach (DurableEntry entry in List<DurableEntry>("entry"))
        {
            document.Entries[DurableRepository.GetAddress(entry.Scope, entry.Key)] = entry;
        }
        foreach (DurableReceipt receipt in List<DurableReceipt>("receipt"))
        {
            document.Receipts[receipt.OperationId] = receipt;
        }
        document.Conflicts.AddRange(List<DurableEntry>("conflict"));
        LoadPrepared(document);
        return document;
    }

    /// <summary>同期でも予約の照合・解放を省略しません。</summary>
    internal void LoadPrepared(RepositoryDocument document)
    {
        foreach (PreparedBatch batch in List<PreparedBatch>("prepared"))
        {
            document.Prepared[batch.OperationId] = batch;
            preparedIds.Add(batch.OperationId);
        }
    }

    /// <summary>共有に必要な履歴だけを読み、既存行の変更検出を維持します。</summary>
    /// <returns>共有履歴、または明示したローカル予約に必要な履歴です。</returns>
    internal IReadOnlyList<DurableReceipt> ReadReceipts(bool exportOnly, IReadOnlySet<string>? localScopes = null)
        => SqliteReceipts.Read(connection, transaction, (key, payload) =>
        {
            if (trackChanges)
            {
                loaded["receipt" + key] = payload;
            }
        }, exportOnly, localScopes)
            .Concat(records.LocalHistoryReceipts(localScopes)).ToArray();

    /// <summary>用途振分けの共有拒否結果だけを現在取引内で読みます。</summary>
    /// <returns>バックエンドへ渡す前に確定した共有結果です。</returns>
    internal IReadOnlyList<Routing.RoutingOwner> ReadRoutingResults() => Routing.SqliteRoutingResults.Read(connection, transaction);

    /// <summary>現在取引で本文指紋または正規化内容を比較し、復号の重複を避けます。</summary>
    /// <returns>実内容を確認できた一致です。</returns>
    internal IReadOnlySet<Guid> MatchingReceipts(IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken)
        => records.MatchingReceipts(receipts, cancellationToken);

    /// <summary>同じ取引で確定結果の一致を確認した履歴は、取込値そのものを比較元として共有します。</summary>
    /// <returns>再復元・再保存する必要のない、確認済みの参照です。</returns>
    internal IReadOnlyDictionary<Guid, DurableReceipt> ReuseReceipts(IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken)
        => records.ReuseReceipts(receipts, cancellationToken);

    /// <summary>変更された行だけを書き、消費済み予約を解放します。</summary>
    public void Save(RepositoryDocument document)
    {
        SqliteHistoryGuard.Save(this, document);
        foreach (DurableEntry entry in document.Entries.Values)
        {
            Write("entry", DurableRepository.GetAddress(entry.Scope, entry.Key), entry.Scope, entry);
        }
        foreach (DurableReceipt receipt in document.Receipts.Values)
        {
            if (!string.Equals(receipt.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal) && !string.Equals(receipt.Reason, Recovery.RecoveryRecordLedger.PendingReason, StringComparison.Ordinal))
            {
                Write("receipt", receipt.OperationId.ToString("N"), SqliteReceipts.Scope(receipt), receipt);
            }
        }
        foreach (Guid removed in preparedIds.Except(document.Prepared.Keys))
        {
            using SqliteCommand command = Create();
            command.CommandText = "DELETE FROM records WHERE kind='prepared' AND key=$id; DELETE FROM reservations WHERE operation=$id;";
            command.Parameters.AddWithValue("$id", removed.ToString("N"));
            command.ExecuteNonQuery();
        }
        foreach (KeyValuePair<Guid, PreparedBatch> pair in document.Prepared)
        {
            string id = pair.Key.ToString("N");
            Write("prepared", id, SqlitePreparations.Scope(pair.Value), pair.Value);
            foreach (DurableMutation mutation in pair.Value.Mutations)
            {
                using SqliteCommand command = Create();
                command.CommandText = "INSERT INTO reservations(address,operation) VALUES($address,$id) ON CONFLICT(address) DO NOTHING";
                command.Parameters.AddWithValue("$address", DurableRepository.GetAddress(mutation.Scope, mutation.Key));
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
        }
        foreach (DurableEntry entry in document.Conflicts)
        {
            Write("conflict", DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version, entry.Scope, entry);
        }
        foreach (string removed in conflictKeys.Except(document.Conflicts.Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version)))
        {
            using SqliteCommand command = Create();
            command.CommandText = "DELETE FROM records WHERE kind='conflict' AND key=$key";
            command.Parameters.AddWithValue("$key", removed);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>全変更を永続確定します。</summary>
    public void Commit() => State.DurableStateNotifications.Commit(path, stateChanges?.Values, transaction.Commit);

    /// <summary>保存先固有の履歴保守も、通常行と同じ取引の中で確定します。</summary>
    /// <returns>呼出元が破棄する、この取引に所属したcommandです。</returns>
    internal SqliteCommand HistoryCommand() => Create();

    /// <summary>展開保守でも通常参照と同じ内容照合を使用します。</summary>
    /// <returns>一ブロック内の元の行です。</returns>
    internal HistoryRow[] ArchivedBlock(string id) => records.ArchivedBlock(id);

    /// <summary>診断値の全体文字列を作らず、現在取引で原子的に置換します。</summary>
    internal void WriteJsonSnapshot(string scope, string key, Action<Utf8JsonWriter> writeValue, CancellationToken cancellationToken)
    {
        if (!SqliteHistoryCandidates.IsHistoryScope(scope) && SqliteJsonSnapshot.SupportsStreaming(connection, transaction))
        {
            SqliteJsonSnapshot.Write(connection, transaction, scope, key, writeValue, cancellationToken);
            return;
        }
        DurableEntry entry = new(scope, key, Guid.NewGuid().ToString("N"), LocalJsonSnapshots.Materialize(writeValue));
        Write("entry", DurableRepository.GetAddress(scope, key), scope, entry);
    }

    /// <summary>ローカルJSON行を検証し、全体DOMを介さず用途の型で読みます。</summary>
    /// <returns>復元した値、未保存ならnullです。</returns>
    internal T? ReadJsonSnapshot<T>(string scope, string key, CancellationToken cancellationToken) where T : class
    {
        if (!SqliteHistoryCandidates.IsHistoryScope(scope) && SqliteJsonSnapshot.SupportsStreaming(connection, transaction))
        {
            return SqliteJsonSnapshot.Read<T>(connection, transaction, scope, key, cancellationToken);
        }
        return LocalJsonSnapshots.Decode<T>(Read<DurableEntry>("entry", DurableRepository.GetAddress(scope, key)));
    }

    /// <summary>実内容の検証を省略しない共有版キャッシュの変更識別です。</summary>
    /// <returns>検証済み行集合、または通常読取りが必要なnullです。</returns>
    internal SqliteSharedFingerprint.VerifiedRows? SharedFingerprint() => SqliteSharedFingerprint.Read(connection, transaction);

    /// <summary>同じ取引の最新復旧点を共有し、本文の全走査を履歴件数分繰り返しません。</summary>
    /// <returns>対象・種類・保存領域ごとの最新スナップショットかです。</returns>
    internal bool IsLatestSnapshot(string scope, string key) => historyHeads.ContainsSnapshot(DurableRepository.GetAddress(scope, key));

    /// <summary>同じ取引の現在定義を、過去の版ごとに復元し直さず確認します。</summary>
    /// <returns>保存版または反映版に対応する履歴かです。</returns>
    internal bool IsCurrentRevision(string scope, string key, string revision) => historyHeads.ContainsRevision(scope, key, revision);

    /// <summary>取引と接続を閉じます。</summary>
    public void Dispose()
    {
        transaction.Dispose();
        connection.Dispose();
    }

    /// <summary>対象行を必要時だけ更新します。</summary>
    private void Write<T>(string kind, string key, string? scope, T value)
    {
        string entryKind = "entry";
        if (records.Write(kind, key, scope, value) && string.Equals(kind, entryKind, StringComparison.Ordinal)
            && value is DurableEntry entry && State.DurableStateNotifications.IsState(scope))
        {
            (stateChanges ??= new(StringComparer.Ordinal))[key] = entry;
        }
    }

    /// <summary>現在取引に所属するcommandを作ります。</summary>
    /// <returns>呼出元が破棄するcommand。</returns>
    private SqliteCommand Create()
    {
        return new SqliteCommand
        {
            Connection = connection,
            Transaction = transaction,
        };
    }
}
