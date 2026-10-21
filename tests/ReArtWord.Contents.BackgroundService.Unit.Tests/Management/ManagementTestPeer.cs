using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>同じCoreの保存・実行サービスを実SQLiteとUDPで構成します。</summary>
internal sealed class ManagementTestPeer : IAsyncDisposable
{
    private readonly WorkflowLogService log;

    /// <summary>試験専用の明示ポートと保存先を使用します。</summary>
    internal ManagementTestPeer(string root, string clusterId, string peerId, int port, bool fresh, Func<SqliteRepository, IDurableRepository>? decorate = null, AvailabilityOptions? availability = null)
    {
        Directory.CreateDirectory(root);
        Repository = new(Path.Combine(root, "state.db"));
        ClusterOptions options = new(clusterId, peerId, peerId, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromSeconds(3),
        };
        IDurableRepository storage = decorate?.Invoke(Repository) ?? Repository;
        Cluster = new(options, storage);
        SecretStore secrets = new(storage);
        log = new(new(peerId, Path.Combine(root, "logs")), storage, secrets);
        CommonConfiguration configuration = new(options, root, "sqlite", TimeZoneInfo.Utc, [], new FileSystemOptions([root]), new Dictionary<string, IReadOnlyList<string>>()) { Availability = availability ?? new() };
        Runtime = new(configuration, storage, Cluster, secrets, log) { ManagementStarted = true };
        Runs = new(Runtime);
    }

    /// <summary>保存先です。</summary>
    internal SqliteRepository Repository
    {
        get;
    }

    /// <summary>実UDP参加です。</summary>
    internal PeerCluster Cluster
    {
        get;
    }

    /// <summary>Coreの実サービスです。</summary>
    internal ServiceRuntime Runtime
    {
        get;
    }

    /// <summary>手動実行の永続受付です。</summary>
    internal WorkflowRunService Runs
    {
        get;
    }

    /// <summary>所有する実行・通信・ログの順に終了します。</summary>
    /// <returns>所有資源の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await ((IAsyncDisposable)Runs).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)Runtime).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)Cluster).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)log).DisposeAsync().ConfigureAwait(false);
    }
}
