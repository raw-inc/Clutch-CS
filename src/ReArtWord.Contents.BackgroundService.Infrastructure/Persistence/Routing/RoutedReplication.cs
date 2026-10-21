using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>複製の耐久取引と復旧確認を、元操作の保存媒体へ接続します。</summary>
internal static class RoutedReplication
{
    /// <summary>元操作が準備を所有する媒体だけを照会します。</summary>
    /// <returns>現在も準備中である操作の指紋です。</returns>
    internal static async Task<string?> ReadPreparationAsync(RepositoryRoutes routes, RoutingState state, Guid operationId, CancellationToken cancellationToken = default)
    {
        RoutingOwner? owner = await state.OwnerAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (owner is null || owner.Result is not null)
        {
            return null;
        }
        return await routes.Stores[owner.Backend].ReadPreparationAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>どの共有媒体の変更も見落とさず、未対応なら従来の読取りを継続させます。</summary>
    /// <returns>共有先の世代を合わせた通知位置です。</returns>
    internal static async Task<string?> ReadStampAsync(RepositoryRoutes routes, CancellationToken cancellationToken = default)
    {
        List<string> stamps = [];
        foreach (IDurableRepository store in routes.SharedStores.Distinct())
        {
            if (store is not ISharedChangeStamp source || await source.ReadSharedChangeStampAsync(cancellationToken).ConfigureAwait(false) is not { } stamp)
            {
                return null;
            }
            stamps.Add(stamp);
        }
        return stamps.Count == 0 ? null
                                 : string.Join('|', stamps);
    }

    /// <summary>元操作と判断が同じ媒体に属する場合だけ、原子的な確定へ接続します。</summary>
    /// <returns>元の局所結果、または未変更のnullです。</returns>
    internal static async Task<Receipts.DurableReceipt?> TryCommitAsync(RepositoryRoutes routes, RoutingState state, ReplicationCommit commit, CancellationToken cancellationToken = default)
    {
        RoutingOwner? owner = await state.OwnerAsync(commit.OperationId, cancellationToken).ConfigureAwait(false);
        if (owner is null || owner.Result is not null || owner.Backend != routes.Backend(commit.Decision.Scope)
            || routes.Stores[owner.Backend] is not IReplicationTransactionStore store)
        {
            return null;
        }
        return await store.TryCommitAsync(commit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共有媒体と判断を持つ既定媒体から、同じ確認種類を有限件数だけ取得します。</summary>
    /// <returns>重複を除いた巡回対象です。</returns>
    internal static async Task<IReadOnlyList<Guid>> ReadIdsAsync(RepositoryRoutes routes, Guid after, int maximum, bool recovery, CancellationToken cancellationToken = default)
    {
        List<Guid> ids = [];
        foreach (IPreparedOperations store in routes.SharedStores.Append(routes.Stores[routes.Default]).Distinct().OfType<IPreparedOperations>())
        {
            IReadOnlyList<Guid> current;
            if (recovery)
            {
                current = await store.ReadRecoveryIdsAsync(after, maximum, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                current = await store.ReadPreparedIdsAsync(after, maximum, cancellationToken).ConfigureAwait(false);
            }
            ids.AddRange(current);
        }
        return ids.Distinct().Order().Take(maximum).ToArray();
    }

    /// <summary>保存先の所有確認を既存経路で行い、媒体をまたぐ構成には変更を加えません。</summary>
    /// <returns>耐久保存の結果、または従来経路を使うnullです。</returns>
    internal static async Task<ReplicationStartResult?> TryAsync(RepositoryRoutes routes, ReplicationStart start,
        Func<Guid, string, IReadOnlyList<string>, CancellationToken, Task<RoutingOwner>> claim, CancellationToken cancellationToken = default)
    {
        if (routes.Select(start.Mutations.Concat(start.Initialization).Select(item => item.Scope)) != routes.Default
            || routes.Stores[routes.Default] is not SqliteRepository sqlite)
        {
            return null;
        }
        string[] initializationScopes = Scopes(start.Initialization), preparationScopes = Scopes(start.Mutations);
        RoutingOwner initialization = await claim(start.InitializationId, start.InitializationFingerprint, initializationScopes, cancellationToken).ConfigureAwait(false);
        RoutingOwner preparation = await claim(start.OperationId, start.Fingerprint, preparationScopes, cancellationToken).ConfigureAwait(false);
        if (initialization.Result is not null || preparation.Result is not null || initialization.Backend != routes.Default || preparation.Backend != routes.Default)
        {
            return null;
        }
        return SqliteReplicationTransactions.Write(sqlite.HistoryPath, start, initialization, preparation, cancellationToken);
    }

    /// <summary>通常の保存と同じ順序で操作の用途を固定します。</summary>
    /// <returns>重複のない用途です。</returns>
    private static string[] Scopes(IEnumerable<DurableMutation> mutations)
        => mutations.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
