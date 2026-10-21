using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ZLinq;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>共有値・操作結果・所有情報を同じ固定区分で照合し、一致する本文を再送しません。</summary>
internal static class SyncInventory
{
    /// <summary>本文件数によって確認メッセージを増大させない区分数です。</summary>
    internal static int BucketCount => 256;

    /// <summary>区分指紋と通常の配送封筒を収める最小の一メッセージ容量です。</summary>
    internal static int MinimumMessageBytes => 32768;

    /// <summary>履歴の全種類で非圧縮区間が重なる場合だけ、その区間の差分として扱います。</summary>
    /// <returns>初回移行前同士、または共通区間があるかです。</returns>
    internal static bool Overlaps(Persistence.History.HistoryRetentionBoundary? left, Persistence.History.HistoryRetentionBoundary? right)
    {
        if (left is null && right is null)
        {
            return true;
        }
        if (left is null || right is null)
        {
            return false;
        }
        return Enum.GetValues<Contracts.Retention.ERetentionCategory>().Where(category => !Persistence.History.HistoryRetentionPolicy.IsLog(category))
            .All(category => left.RawBefore.GetValueOrDefault(category) <= right.ObservedAt && right.RawBefore.GetValueOrDefault(category) <= left.ObservedAt);
    }

    /// <summary>同じ固定スナップショットの全区分について内容の指紋を返します。</summary>
    /// <returns>順序に依存しない256個の完全ハッシュです。</returns>
    internal static string[] Describe(RepositorySnapshot snapshot)
        => Describe(snapshot, snapshot.Receipts.Select(receipt => (receipt.OperationId, ContractJson.Hash(receipt))));

    /// <summary>固定取引の既知本文はキャッシュした指紋を使い、過去本文を一括復元しません。</summary>
    /// <returns>従来と同じ256区分の完全ハッシュです。</returns>
    internal static string[] Describe(Persistence.SnapshotExport export)
        => Describe(export.Snapshot, export.Receipts.Select(receipt => (receipt.OperationId, receipt.Hash())));

    /// <summary>媒体の読取り方法にかかわらず、区分・並び順・ハッシュ契約を統一します。</summary>
    /// <returns>全区分の内容識別子です。</returns>
    internal static string[] Describe(RepositorySnapshot snapshot, IEnumerable<(Guid Id, string Hash)> receipts)
    {
        List<string>[] buckets = Enumerable.Range(0, BucketCount).Select(_ => new List<string>()).ToArray();
        foreach (Persistence.DurableEntry entry in snapshot.Entries)
        {
            buckets[EntryBucket(entry)].Add("entry:" + ContractJson.Hash(entry));
        }
        foreach ((Guid id, string hash) in receipts)
        {
            buckets[ReceiptBucket(id)].Add("receipt:" + hash);
        }
        foreach (Contracts.Persistence.PersistenceOperationOwner owner in snapshot.OperationOwners ?? [])
        {
            buckets[ReceiptBucket(owner.OperationId)].Add("owner:" + ContractJson.Hash(owner));
        }
        foreach (Persistence.Causality.RepositoryCausalEntry entry in snapshot.CausalEntries ?? [])
        {
            buckets[Bucket("entry:" + entry.Scope + "\0" + entry.Key)].Add("causal:" + ContractJson.Hash(entry));
        }
        foreach (Persistence.Causality.RepositoryLegacyEntry entry in snapshot.LegacyEntries ?? [])
        {
            buckets[Bucket("entry:" + entry.Scope + "\0" + entry.Key)].Add("legacy:" + ContractJson.Hash(entry));
        }
        foreach (Persistence.History.RetiredOperation operation in snapshot.RetiredOperations ?? [])
        {
            buckets[ReceiptBucket(operation.Id)].Add("retired:" + ContractJson.Hash(operation));
        }
        foreach (Persistence.History.RetiredHistoryKey key in snapshot.RetiredKeys ?? [])
        {
            buckets[Bucket("entry:" + key.Scope + "\0" + key.Key)].Add("retired-key:" + ContractJson.Hash(key));
        }
        return buckets.AsValueEnumerable().Select(bucket => ContractJson.Hash(bucket.AsValueEnumerable().Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    /// <summary>照合内容の欠損や無制限な区分配列を転送前に拒否します。</summary>
    internal static void Validate(IReadOnlyList<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (hashes.Count != BucketCount || hashes.Any(hash => hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)))
        {
            throw new SyncFailureException("Sync.InventoryMismatch");
        }
    }

    /// <summary>一致区分の本文を保持せず、同期境界と保護情報だけを残します。</summary>
    /// <returns>全区分が一致したときも送る小さいメタデータです。</returns>
    internal static RepositorySnapshot Metadata(RepositorySnapshot snapshot) => snapshot with
    {
        Entries = [], Receipts = [], OperationOwners = Empty(snapshot.OperationOwners), CausalEntries = Empty(snapshot.CausalEntries),
        LegacyEntries = Empty(snapshot.LegacyEntries), RetiredOperations = Empty(snapshot.RetiredOperations), RetiredKeys = Empty(snapshot.RetiredKeys),
        HistoryBoundary = snapshot.HistoryBoundary is null ? null
                                                           : ContractJson.Copy(snapshot.HistoryBoundary),
        ProtectedHistory = snapshot.ProtectedHistory?.ToArray(), PendingOperations = snapshot.PendingOperations?.ToArray(),
        RecoveredHistory = Empty(snapshot.RecoveredHistory),
    };

    /// <summary>旧契約で省略された配列と、空の配列を区別して本文だけを除きます。</summary>
    /// <returns>同じ省略状態の空列です。</returns>
    private static IReadOnlyList<T>? Empty<T>(IReadOnlyList<T>? values)
    {
        if (values is null)
        {
            return null;
        }
        return [];
    }

    /// <summary>元の固定版から、相手と内容が異なる区分だけを選びます。</summary>
    /// <returns>祖先の操作記録と所有先も含む、既存Importで検証可能な差分です。</returns>
    internal static RepositorySnapshot Difference(RepositorySnapshot snapshot, IReadOnlyList<string> local, IReadOnlyList<string> remote)
    {
        Validate(local);
        Validate(remote);
        bool[] changed = Enumerable.Range(0, BucketCount).Select(index => !string.Equals(local[index], remote[index], StringComparison.OrdinalIgnoreCase)).ToArray();
        return snapshot with
        {
            Entries = snapshot.Entries.Where(entry => changed[EntryBucket(entry)]).ToArray(),
            Receipts = snapshot.Receipts.Where(receipt => changed[ReceiptBucket(receipt.OperationId)]).ToArray(),
            OperationOwners = snapshot.OperationOwners?.Where(owner => changed[ReceiptBucket(owner.OperationId)]).ToArray(),
            CausalEntries = snapshot.CausalEntries?.Where(entry => changed[Bucket("entry:" + entry.Scope + "\0" + entry.Key)]).ToArray(),
            LegacyEntries = snapshot.LegacyEntries?.Where(entry => changed[Bucket("entry:" + entry.Scope + "\0" + entry.Key)]).ToArray(),
            RetiredOperations = snapshot.RetiredOperations?.Where(operation => changed[ReceiptBucket(operation.Id)]).ToArray(),
            RetiredKeys = snapshot.RetiredKeys?.Where(key => changed[Bucket("entry:" + key.Scope + "\0" + key.Key)]).ToArray(),
        };
    }

    /// <summary>同じキーの現在値と競合候補を同じ区分へ入れます。</summary>
    /// <returns>固定したキーの区分です。</returns>
    internal static int EntryBucket(Persistence.DurableEntry entry) => KeyBucket(entry.Scope, entry.Key);

    /// <summary>現在値、因果位置、削除済みキーを同じ区分へ配置します。</summary>
    /// <returns>同じ論理キーの固定区分です。</returns>
    internal static int KeyBucket(string scope, string key) => Bucket("entry:" + scope + "\0" + key);

    /// <summary>操作結果と論理所有先を分けずに転送します。</summary>
    /// <returns>元操作の区分です。</returns>
    internal static int ReceiptBucket(Guid id) => Bucket("receipt:" + id.ToString("N"));

    /// <summary>実行環境で変化する文字列hashを区分選択に使用しません。</summary>
    /// <returns>SHA-256の先頭バイトです。</returns>
    private static int Bucket(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key))[0];
}
