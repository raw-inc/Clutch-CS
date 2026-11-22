using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>通常・差分・準備済み差分・段階同期と背景履歴を、独立した時計の実Peer間で確認します。</summary>
public sealed class ClockSkewSynchronizationTests
{
    /// <summary>UTCが前後1時間ずれていても、現在値と元操作の結果を同期し、その後も共有保存できます。</summary>
    /// <returns>隔離したUDP/TCP、JSON/SQLiteの両方向同期です。</returns>
    [Theory]
    [InlineData(0, -3600)]
    [InlineData(0, 3600)]
    [InlineData(1, -3600)]
    [InlineData(1, 3600)]
    [InlineData(2, -3600)]
    [InlineData(2, 3600)]
    [InlineData(3, -3600)]
    [InlineData(3, 3600)]
    public async Task DifferentWallClocksPreserveAllSynchronizationPathsAsync(int mode, int offsetSeconds)
    {
        using MediaTestFiles files = new();
        IDurableRepository left = mode is 2 or 3 ? new SqliteRepository(Path.Combine(files.Root, "left.db"))
                                                 : new JsonFileRepository(Path.Combine(files.Root, "left.json"));
        IDurableRepository right = mode == 2 ? new SqliteRepository(Path.Combine(files.Root, "right.db"))
                                             : new JsonFileRepository(Path.Combine(files.Root, "right.json"));
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        int maximumBytes = mode == 0 ? 8192
                                     : 65536;
        WallClock leftClock = new(-offsetSeconds), rightClock = new(offsetSeconds);
        PeerCluster source = Create(left, files.Root, cluster, "source", port, true, maximumBytes, leftClock);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(false);
        PeerCluster target = Create(right, files.Root, cluster, "target", port, false, maximumBytes, rightClock);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = target.ConfigureAwait(false);
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(40));
        await source.StartAsync(watchdog.Token);
        List<Guid> operations = [];
        for (int index = 0; index < 8; index++)
        {
            Guid operation = Guid.CreateVersion7();
            operations.Add(operation);
            Assert.Equal(EOutcome.Succeeded, (await source.ReplicateAsync(operation,
                [new("workflow:clock-sync", "value-" + index, null, NodeValue.FromObject(new string('x', 512)))], watchdog.Token)).Outcome);
        }
        await target.StartAsync(watchdog.Token);
        while (source.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || target.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2
            || await right.ReadReceiptAsync(operations[^1], watchdog.Token) is not { Outcome: EOutcome.Succeeded }
            || target.HistorySynchronization is { Pending: > 0 } || source.SynchronizationProblem is not null || target.SynchronizationProblem is not null)
        {
            await Task.Delay(20, watchdog.Token);
        }
        foreach (Guid operation in operations)
        {
            Assert.Equal(EOutcome.Succeeded, (await right.ReadReceiptAsync(operation, watchdog.Token))!.Outcome);
        }
        Assert.Equal(await left.GetSharedVersionAsync(watchdog.Token), await right.GetSharedVersionAsync(watchdog.Token));
        Guid next = Guid.CreateVersion7();
        Assert.Equal(EOutcome.Succeeded, (await target.ReplicateAsync(next, [new("workflow:clock-sync", "after-sync", null, NodeValue.FromObject(42))], watchdog.Token)).Outcome);
        Assert.Equal(42, (await left.ReadAsync("workflow:clock-sync", "after-sync", watchdog.Token))!.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Succeeded, (await left.ReadReceiptAsync(next, watchdog.Token))!.Outcome);
    }

    /// <summary>通知と配送は実時間で動かし、UTCのずれだけを注入します。</summary>
    private sealed class WallClock(int offsetSeconds) : TimeProvider
    {
        /// <summary>記録・表示用の日時だけをずらします。</summary>
        /// <returns>PCごとのUTCです。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddSeconds(offsetSeconds);
    }

    /// <summary>実体・保存先を分け、通常同期の既定上限内で実通信を行います。</summary>
    /// <returns>試験が所有するCoreの通信部分です。</returns>
    private static PeerCluster Create(IDurableRepository repository, string root, string cluster, string peer, int port, bool fresh, int maximumBytes, TimeProvider clock)
    {
        int priority = fresh ? 2
                             : 1;
        return new(new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, priority, fresh, root)
        { MaximumMessageBytes = maximumBytes, HeartbeatInterval = TimeSpan.FromMilliseconds(100), RequestTimeout = TimeSpan.FromSeconds(2), SyncSessionLifetime = TimeSpan.FromSeconds(30) }, repository, clock)
        { ReadinessCheck = token => ValueTask.FromResult(true) };
    }
}
