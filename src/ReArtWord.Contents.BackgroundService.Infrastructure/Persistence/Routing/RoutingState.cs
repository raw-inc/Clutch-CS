using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>既存default媒体にrouting設定と操作所有先をローカル永続保存します。</summary>
internal sealed class RoutingState(IDurableRepository repository)
{
    private static string Scope => "peer:persistence-routing";

    /// <summary>未完了Importの固定内容ハッシュです。</summary>
    /// <returns>処理中でなければnullです。</returns>
    internal async Task<string?> PendingAsync(CancellationToken cancellationToken = default)
    {
        return (await repository.ReadAsync(Scope, "import", cancellationToken).ConfigureAwait(false))?.Value?.GetString();
    }

    /// <summary>保存方式変更による無言の別保存先利用を拒否します。</summary>
    /// <returns>設定固定の完了です。</returns>
    internal async Task InitializeAsync(string peerId, PersistenceOptions options, bool legacyExists, CancellationToken cancellationToken = default)
    {
        DurableEntry? identity = await repository.ReadAsync("peer:bootstrap", "identity", cancellationToken).ConfigureAwait(false);
        PeerStorageIdentity? owner = identity?.Value?.Json.Deserialize<PeerStorageIdentity>(ContractJson.Options);
        if (identity is not null && !string.Equals(owner?.PeerId, peerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("既存の保存所有Peerと用途割当のPeerが異なります。");
        }
        string hash = ContractJson.Hash(new { peerId, options });
        string? previous = (await repository.ReadAsync(Scope, "configuration", cancellationToken).ConfigureAwait(false))?.Value?.GetString();
        if (previous is null)
        {
            if (legacyExists && options.Purposes?.Values.Any(backend => backend != options.DefaultBackend) == true)
            {
                throw new InvalidDataException("既存の単一保存領域を用途別に移すには明示した移行が必要です。");
            }
            await WriteAsync("configuration", NodeValue.FromObject(hash), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!string.Equals(previous, hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存済み用途割当と設定が異なります。自動移行は行いません。");
            }
        }
    }

    /// <summary>default媒体の変更で既存の割当を見失わないために確認します。</summary>
    /// <returns>保存済みの割当ハッシュです。</returns>
    internal async Task<string?> ConfigurationAsync(CancellationToken cancellationToken = default) => (await repository.ReadAsync(Scope, "configuration", cancellationToken).ConfigureAwait(false))?.Value?.GetString();

    /// <summary>元operationIdの永続所有先を読みます。</summary>
    /// <returns>未受理ならnullです。</returns>
    internal async Task<RoutingOwner?> OwnerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, "owner:" + id.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }
        return ReadOwner(entry, id);
    }

    /// <summary>共有スナップショットの件数分、DBを開き直したりJSON全体を読み直したりしません。</summary>
    /// <returns>要求した操作だけの確認済み所有先です。</returns>
    internal async Task<IReadOnlyList<RoutingOwner>> ReadOwnersAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        Dictionary<string, Guid> keys = ids.Distinct().ToDictionary(id => "owner:" + id.ToString("N"));
        if (keys.Count == 0)
        {
            return [];
        }
        IReadOnlyList<DurableEntry> entries;
        if (repository is SqliteRepository sqlite)
        {
            entries = await sqlite.ReadEntriesAsync(Scope, keys.Keys, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            entries = await repository.ListAsync(Scope, cancellationToken).ConfigureAwait(false);
        }
        return entries.Where(entry => keys.ContainsKey(entry.Key)).Select(entry => ReadOwner(entry, keys[entry.Key])).ToArray();
    }

    /// <summary>単項目と一括読取りで同じ所有情報の整合性を検証します。</summary>
    /// <returns>操作IDと保存先が一致する所有情報です。</returns>
    private static RoutingOwner ReadOwner(DurableEntry entry, Guid id)
    {
        RoutingOwner? owner = entry.Value?.Json.Deserialize<RoutingOwner>(ContractJson.Options);
        if (owner?.Operation is null || owner.Operation.OperationId != id || owner.Operation.Scopes is null || owner.Operation.Fingerprint is null || !Enum.IsDefined(owner.Backend))
        {
            throw new InvalidDataException("元操作の保存先が欠損または不正です。");
        }
        return owner;
    }

    /// <summary>変更前に拒否した操作も共有可能な操作記録へ含めるため取得します。</summary>
    /// <returns>ローカルの操作所有先です。</returns>
    internal async Task<IReadOnlyList<RoutingOwner>> OwnersAsync(CancellationToken cancellationToken = default)
    {
        string prefix = "owner:";
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync(Scope, cancellationToken).ConfigureAwait(false);
        return entries.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(entry => entry.Value?.Json.Deserialize<RoutingOwner>(ContractJson.Options) ?? throw new InvalidDataException("操作の保存先が欠損しています。")).ToArray();
    }

    /// <summary>バックエンド履歴の外に確定した共有結果だけを取得します。</summary>
    /// <returns>共有可能な確定拒否結果です。</returns>
    internal async Task<IReadOnlyList<RoutingOwner>> SharedResultsAsync(CancellationToken cancellationToken = default)
    {
        if (repository is SqliteRepository sqlite)
        {
            return await sqlite.ReadRoutingResultsAsync(cancellationToken).ConfigureAwait(false);
        }
        return (await OwnersAsync(cancellationToken).ConfigureAwait(false)).Where(owner => owner.Result is not null && owner.Operation.Scopes.All(DurableRepository.IsShared)).ToArray();
    }

    /// <summary>元操作の所有先を副作用開始前に保存します。</summary>
    /// <returns>保存確認の完了です。</returns>
    internal Task SaveOwnerAsync(RoutingOwner owner, CancellationToken cancellationToken = default) => WriteAsync("owner:" + owner.Operation.OperationId.ToString("N"), NodeValue.FromObject(owner), cancellationToken);

    /// <summary>ローカル情報を通常共有や操作記録履歴へ混入せず原子的に置換します。</summary>
    /// <returns>保存確認の完了です。</returns>
    internal async Task WriteAsync(string key, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (await repository.WriteLocalSnapshotAsync(Scope, key, value, cancellationToken).ConfigureAwait(false) != EOutcome.Succeeded)
        {
            throw new IOException("保存用途の耐久状態を確認できません。");
        }
    }

    /// <summary>移行前の単一媒体操作記録も捨てず、重複した異内容は拒否します。</summary>
    /// <returns>同じ元操作の結果です。</returns>
    internal async Task<Receipts.DurableReceipt?> ReceiptAsync(Guid id, RepositoryRoutes routes, CancellationToken cancellationToken = default)
    {
        RoutingOwner? owner = await OwnerAsync(id, cancellationToken).ConfigureAwait(false);
        if (owner is not null)
        {
            return owner.Result ?? await routes.Stores[owner.Backend].ReadReceiptAsync(id, cancellationToken).ConfigureAwait(false);
        }
        Receipts.DurableReceipt? result = null;
        foreach (IDurableRepository store in routes.Stores.Values)
        {
            Receipts.DurableReceipt? candidate = await store.ReadReceiptAsync(id, cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                if (result is not null && !string.Equals(ContractJson.Hash(result), ContractJson.Hash(candidate), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("同じ操作IDの保存結果が媒体間で一致しません。");
                }
                result = candidate;
            }
        }
        return result;
    }

}
