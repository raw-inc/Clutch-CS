using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>既存SQLite/JSONを用途別にまとめ、ローカル未完了Importを回復して公開します。</summary>
public static class RepositoryFactory
{
    /// <summary>既存state.db/state.jsonを維持して一つの保存境界を作成します。</summary>
    /// <returns>未完了の取込みが回復したリポジトリです。</returns>
    public static Task<IDurableRepository> CreateAsync(string directory, string peerId, PersistenceOptions options, CancellationToken cancellationToken = default)
        => CreateCoreAsync(directory, peerId, options, null, cancellationToken);

    /// <summary>Coreの稼働期間で接続を再利用し、所有者の終了時に解放します。</summary>
    /// <returns>保存処理の終了後に破棄する稼働期間です。</returns>
    // 抑制理由: 公開時にRepositoryRuntimeへ所有権を渡し、初期化失敗時はfinallyで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "返却する稼働期間が接続を所有し、初期化失敗時だけfinallyのusingで解放します。")]
    public static async Task<RepositoryRuntime> CreateRuntimeAsync(string directory, string peerId, PersistenceOptions options, CancellationToken cancellationToken = default)
    {
        SqliteConnectionLifetime connections = new();
        bool transferred = false;
        try
        {
            IDurableRepository repository = await CreateCoreAsync(directory, peerId, options, connections, cancellationToken).ConfigureAwait(false);
            RepositoryRuntime runtime = new(repository, connections);
            transferred = true;
            return runtime;
        }
        finally
        {
            using SqliteConnectionLifetime? abandoned = transferred ? null
                                                                    : connections;
        }
    }

    /// <summary>稼働期間の有無によらず、同じ初期化・取込み回復を適用します。</summary>
    /// <returns>未完了の取込みが回復したリポジトリです。</returns>
    private static async Task<IDurableRepository> CreateCoreAsync(string directory, string peerId, PersistenceOptions options, SqliteConnectionLifetime? lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(directory) || !Enum.IsDefined(options.DefaultBackend) || options.Purposes?.Any(pair => !Enum.IsDefined(pair.Key) || !Enum.IsDefined(pair.Value)) == true)
        {
            throw new ArgumentException("保存先と用途別媒体の指定が不正です。", nameof(options));
        }
        PersistenceOptions owned = new(options.DefaultBackend, Enum.GetValues<EStoragePurpose>().ToDictionary(purpose => purpose, purpose => options.Purposes?.GetValueOrDefault(purpose, options.DefaultBackend) ?? options.DefaultBackend));
        FileAccessPolicy policy = new(new FileSystemOptions([directory]));
        policy.ValidatePath(directory, true);
        Directory.CreateDirectory(directory);
        string sqlitePath = Path.Combine(directory, "state.db");
        string jsonPath = Path.Combine(directory, "state.json");
        bool existingSqlite = File.Exists(sqlitePath);
        bool existingJson = File.Exists(jsonPath);
        bool existing = existingSqlite || existingJson;
        using RoutingLease lease = await RoutingLease.AcquireAsync(Path.Combine(directory, "routing.lock"), cancellationToken).ConfigureAwait(false);
        using SqliteConnectionScope? connections = lifetime is null ? null
                                                                    : new(lifetime);
        Dictionary<EStorageBackend, IDurableRepository> stores = [];
        foreach (EStorageBackend backend in new[] { owned.DefaultBackend }.Concat(owned.Purposes!.Values).Distinct())
        {
            IDurableRepository store = new JsonFileRepository(jsonPath);
            if (backend == EStorageBackend.Sqlite)
            {
                store = new SqliteRepository(sqlitePath);
            }
            stores.Add(backend, store);
        }
        RepositoryRoutes routes = new(peerId, owned, stores);
        RoutingState state = new(stores[owned.DefaultBackend]);
        if (existing && await state.ConfigurationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            bool otherExists = existingJson;
            if (owned.DefaultBackend == EStorageBackend.Json)
            {
                otherExists = existingSqlite;
            }
            if (otherExists)
            {
                throw new InvalidDataException("既存の別default媒体が残っています。自動移行は行いません。");
            }
        }
        await state.InitializeAsync(peerId, owned, existing, cancellationToken).ConfigureAwait(false);
        RoutingSynchronization synchronization = new(directory, routes, state);
        await synchronization.RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (lifetime is not null && stores.ContainsKey(EStorageBackend.Sqlite))
        {
            SqliteConnectionLifetime.EnableLocalJournal(sqlitePath);
        }
        return new RoutedRepository(directory, routes, state, synchronization, lifetime);
    }
}
