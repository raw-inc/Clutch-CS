using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>稼働Coreと排他的な明示操作でだけ、全展開またはDB全体の縮小を行います。</summary>
public static class HistoryOfflineMaintenance
{
    /// <summary>通常の用途割当を検証してから、固定した保存先を一度だけ保守します。</summary>
    /// <returns>処理完了後の件数と実容量です。</returns>
    public static async Task<HistoryArchiveStatus> RunAsync(string directory, string peerId, PersistenceOptions options, bool expand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using FileStream offline = HistoryRuntimeLease.AcquireOffline(directory);
        string path = Path.Combine(directory, "state.db");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("履歴保守の対象となるSQLite保存先がありません。");
        }
        IDurableRepository repository = await RepositoryFactory.CreateAsync(directory, peerId, options, cancellationToken).ConfigureAwait(false);
        using (RoutingLease lease = await RoutingLease.AcquireAsync(Path.Combine(directory, "routing.lock"), cancellationToken).ConfigureAwait(false))
        {
            if (expand)
            {
                while (SqliteHistoryRestore.ExpandOne(path, cancellationToken) != 0)
                {
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                }
            }
            SqliteHistoryRestore.Compact(path, cancellationToken);
        }
        using HistoryArchiveService archive = new(repository);
        return await archive.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }
}
