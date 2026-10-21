using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;

/// <summary>排他的なファイル取引と置換による永続保存です。</summary>
public sealed class JsonFileRepository : DurableRepository, IRequestReceiptStore, State.IDurableStateChanges, IReplicationTransactionStore
{
    private readonly string path;

    /// <summary>JSONの開始処理は既存の順序を維持します。</summary>
    /// <returns>開始前の未対応を示すnullです。</returns>
    ValueTask<ReplicationStartResult?> IReplicationTransactionStore.TryStartAsync(ReplicationStart start, CancellationToken cancellationToken)
        => ValueTask.FromResult<ReplicationStartResult?>(null);

    /// <summary>一回の原子的置換で、元コミットと確定判断を同時に保存します。</summary>
    /// <returns>元操作の局所結果です。</returns>
    ValueTask<DurableReceipt?> IReplicationTransactionStore.TryCommitAsync(ReplicationCommit commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DurableReceipt?>(Access(commit.Apply, true));
    }

    /// <summary>この保存先の確定した状態変更だけを購読します。</summary>
    /// <returns>呼出し元が解放する購読です。</returns>
    public State.DurableStateSubscription SubscribeState(string scope, string key) => State.DurableStateNotifications.Subscribe(path, scope, key);

    /// <summary>明示した保存先を作成します。既存の破損は初期化しません。</summary>
    public JsonFileRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>JSON本文と圧縮履歴を同じ原子的保存で保守します。</summary>
    /// <returns>削除した確定本文の件数です。</returns>
    internal int MaintainHistory(Contracts.Retention.DataRetentionSettings policy, DateTimeOffset now, int maximum)
        => Access(document => JsonHistoryMaintenance.Run(document, policy, now, maximum), true);

    /// <summary>大きい基準点本文を画面へ返さず保存状態だけを列挙します。</summary>
    /// <returns>新しい順の基準点です。</returns>
    internal Contracts.Persistence.History.HistoryCheckpointInfo[] HistoryCheckpoints()
        => Access(document => document.History?.Checkpoints.Select(checkpoint => checkpoint.Info).Reverse().Take(100).ToArray() ?? [], false);

    /// <summary>JSONの実ファイルと圧縮本文の保存量を読みます。</summary>
    /// <returns>SQLiteを使わない構成の保守状態です。</returns>
    internal Contracts.Persistence.History.HistoryArchiveStatus HistoryStatus(string? problem)
        => Access(document => new Contracts.Persistence.History.HistoryArchiveStatus(true,
            document.Receipts.Count + document.Entries.Values.Count(entry => HistoryRetentionPolicy.Category("entry", entry.Scope) is not null),
            document.History?.Archives.Count ?? 0, document.History?.Archives.Count ?? 0,
            document.History?.Archives.Values.Sum(row => (long)row.Data.Length) ?? 0, document.History?.Archives.Values.Sum(row => (long)row.Length) ?? 0,
            File.Exists(path) ? new FileInfo(path).Length
                              : 0, 0, problem), false);

    /// <summary>既存のプロセス間排他と原子的ファイル置換で、要求の現在値だけを更新します。</summary>
    /// <returns>確定した項目、またはCAS不一致です。</returns>
    ValueTask<DurableEntry?> IRequestReceiptStore.CompareExchangeReceiptAsync(string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken)
    {
        RequestReceiptPersistence.Validate(scope, key, value);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access<DurableEntry?>(document =>
        {
            string address = GetAddress(scope, key);
            DurableEntry? previous = document.Entries.GetValueOrDefault(address) ?? JsonHistoryAccess.Read<DurableEntry>(document, "entry", address);
            if (!string.Equals(previous?.Version, expectedVersion, StringComparison.Ordinal) || IsReserved(document, new(scope, key, expectedVersion, value))
                || previous is null && (document.History?.RetiredKeys.Contains(address) == true || Guid.TryParse(key, out Guid id)
                    && RepositoryHistoryGuard.Expired(id, RepositoryHistoryGuard.Read(document)) is not null))
            {
                return null;
            }
            DurableEntry entry = new(scope, key, Guid.NewGuid().ToString("N"), value.Copy(), expectedVersion);
            document.Entries[address] = entry;
            return entry;
        }, true));
    }

    /// <summary>同期履歴の照合で保存JSON全体を項目ごとに再読込みしません。</summary>
    /// <returns>一つの読取り取引で内容一致を確認した操作IDです。</returns>
    internal ValueTask<IReadOnlySet<Guid>> MatchingReceiptsAsync(IReadOnlyList<DurableReceipt> receipts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Access<IReadOnlySet<Guid>>(document =>
        {
            HashSet<Guid> matched = [];
            foreach (DurableReceipt receipt in receipts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DurableReceipt? old = JsonHistoryAccess.Receipt(document, receipt.OperationId);
                if (old is not null && string.Equals(Contracts.Serialization.ContractJson.Hash(old), Contracts.Serialization.ContractJson.Hash(receipt), StringComparison.Ordinal))
                {
                    matched.Add(receipt.OperationId);
                }
            }
            return matched;
        }, false));
    }

    /// <summary>別プロセスの保存操作と排他して文書を読み書きします。</summary>
    /// <returns>確認した保存結果です。</returns>
    private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
    {
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        using Mutex mutex = new(false, "ContentsStore-" + identity);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (!acquired)
        {
            throw new IOException("保存先の取引を開始できません。");
        }
        try
        {
            return AccessLocked(operation, write);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>別プロセスを含む排他区間で比較更新します。</summary>
    /// <returns>取引結果。</returns>
    private T AccessLocked<T>(Func<RepositoryDocument, T> operation, bool write)
    {
        // 別インスタンス・別プロセスも同じロックを使い、CASをread/writeに分断しません。
        using FileStream guard = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        bool exists = File.Exists(path);
        string markerPath = path + ".initialized";
        if (!exists && File.Exists(markerPath))
        {
            throw new InvalidDataException("初期化済み保存先の本体がありません。");
        }
        RepositoryDocument document = new();
        byte[]? previous = null;
        if (exists)
        {
            previous = File.ReadAllBytes(path);
            document = RepositoryCodec.Decode(previous);
        }
        HashSet<Guid>? known = null;
        if (write && document.History is not null)
        {
            known = document.Receipts.Keys.ToHashSet();
        }
        Dictionary<string, string?>? observed = write ? State.DurableStateNotifications.Capture(path, document)
                                                      : null;
        T result = operation(document);
        if (write)
        {
            if (known is not null)
            {
                JsonHistoryCheckpoints.Record(document, known, DateTimeOffset.UtcNow);
            }
            JsonHistoryAccess.Reconcile(document);
            byte[] content = RepositoryCodec.Encode(document);
            if (previous is not null && content.AsSpan().SequenceEqual(previous))
            {
                return result;
            }
            string staging = path + ".staging";
            using (FileStream output = new(staging, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(content);
                output.Flush(true);
            }
            File.Move(staging, path, true);
            using FileStream marker = new(path + ".initialized", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
            marker.Flush(true);
            State.DurableStateNotifications.Publish(path, document, observed);
        }
        return result;
    }
}
