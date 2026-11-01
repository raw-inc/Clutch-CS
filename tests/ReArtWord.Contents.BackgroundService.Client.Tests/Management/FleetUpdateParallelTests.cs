using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>並列上限と可用性、進捗保存と未確認時の開始境界を検証します。</summary>
public sealed class FleetUpdateParallelTests
{
    /// <summary>指定した並列数で更新され、進捗と試行結果が欠けずに保存されることを確認します。</summary>
    /// <returns>同時に止まった実体数と全結果の確認です。</returns>
    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public async Task FillsSlotsWithoutOverlappingJournalWrites(int limit)
    {
        FleetUpdateStatus initial = Status(Enumerable.Range(0, 6).Select(index => Target(index.ToString(System.Globalization.CultureInfo.InvariantCulture), index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(), limit);
        FleetUpdateTestRemote remote = new(initial.Request.Targets);
        TaskCompletionSource filled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, peak = 0, saving = 0, attempts = 0;
        remote.AfterDispatch = async (_, token) =>
        {
            int count = Interlocked.Increment(ref active);
            Assert.InRange(count, 1, limit);
            if (count == limit)
            {
                Interlocked.Exchange(ref peak, count);
                filled.TrySetResult();
            }
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Decrement(ref active);
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<FleetUpdateStatus> run = new FleetUpdateRunner(remote).RunAsync(initial, async (status, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref saving));
            await Task.Yield();
            int current = status.Peers.Sum(peer => peer.Attempts.Count);
            Assert.True(attempts <= current);
            attempts = current;
            Interlocked.Decrement(ref saving);
        }, timeout.Token);
        try
        {
            await filled.Task.WaitAsync(timeout.Token);
            Assert.Equal(limit, remote.Observations.Values.Count(peer => !peer.Ready));
        }
        finally
        {
            release.TrySetResult();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        FleetUpdateStatus result = await run.ConfigureAwait(true);
        Assert.Equal(limit, peak);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.All(result.Peers, peer => Assert.Single(peer.Attempts));
        Assert.Equal(6, remote.Disposals);
    }

    /// <summary>PCまたはクラスタが重なる候補を飛ばし、別の安全な対象を開始します。</summary>
    /// <returns>各開始時の排他と実際の並列利用です。</returns>
    [Fact]
    public async Task SkipsConflictingHostsAndClustersAndReusesSlotsAfterRecovery()
    {
        FleetUpdateStatus initial = Status([Target("a", "one"), Target("a", "two"), Target("b", "one"), Target("c", "three")], 3);
        FleetUpdateTestRemote remote = new(initial.Request.Targets);
        TaskCompletionSource firstPair = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentDictionary<string, FleetUpdateTarget> active = [];
        remote.AfterDispatch = async (target, token) =>
        {
            string key = target.ClusterId + "/" + target.Peer.PeerId;
            Assert.True(active.TryAdd(key, target));
            FleetUpdateTarget[] current = active.Values.ToArray();
            Assert.Equal(current.Length, current.Select(peer => peer.ClusterId).Distinct().Count());
            Assert.Equal(current.Length, current.Select(peer => peer.Peer.HostId).Distinct().Count());
            if (current.Length == 2)
            {
                firstPair.TrySetResult();
            }
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            active.TryRemove(key, out _);
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<FleetUpdateStatus> run = new FleetUpdateRunner(remote).RunAsync(initial, (_, _) => Task.CompletedTask, timeout.Token);
        try
        {
            await firstPair.Task.WaitAsync(timeout.Token);
            Assert.Equal(["dispatch:a/one", "dispatch:c/three"], remote.Events.Where(item => item.StartsWith("dispatch:", StringComparison.Ordinal)));
        }
        finally
        {
            release.TrySetResult();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>3ピア以上では同一PC構成も含め、停止対象を除いた過半数が必要です。</summary>
    /// <returns>準備前と停止直前のReady再検査です。</returns>
    [Theory]
    [InlineData(3, 1, false, false)]
    [InlineData(3, 1, true, false)]
    [InlineData(4, 1, false, false)]
    [InlineData(5, 1, false, true)]
    public async Task RequiresMajorityEvenWhenAPeerFailsDuringPreparation(int count, int unavailable, bool duringStage, bool allowed)
    {
        FleetUpdateStatus initial = Status(Enumerable.Range(0, count).Select(index => Target(index.ToString(System.Globalization.CultureInfo.InvariantCulture), "one")).ToArray(), 3);
        FleetUpdateTestRemote remote = new(initial.Request.Targets);
        void LosePeer()
        {
            foreach (FleetUpdateTarget peer in initial.Request.Targets.TakeLast(unavailable))
            {
                remote.Observations[("one", peer.Peer.PeerId)] = remote.Observations[("one", peer.Peer.PeerId)] with { Ready = false };
            }
        }
        if (duringStage)
        {
            remote.BeforeStage = _ => LosePeer();
        }
        else
        {
            LosePeer();
        }
        FleetUpdateStatus result = await new FleetUpdateRunner(remote).RunAsync(initial, (_, _) => Task.CompletedTask);
        Assert.Equal(allowed, result.Peers[0].State is "Completed");
        Assert.Equal(EOutcome.Failed, result.Outcome);
        if (!allowed)
        {
            Assert.DoesNotContain(remote.Events, item => item.StartsWith("dispatch:", StringComparison.Ordinal));
        }
    }

    /// <summary>結果不明後も他の開始済み更新を回収し、空いた枠で次を開始しません。</summary>
    /// <returns>未着手と確認済み結果を失わない停止です。</returns>
    [Fact]
    public async Task UnknownResultStopsNewStartsButCollectsOtherRunningResults()
    {
        FleetUpdateStatus initial = Status(Enumerable.Range(0, 5).Select(index => Target(index.ToString(System.Globalization.CultureInfo.InvariantCulture), index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(), 3);
        FleetUpdateTestRemote remote = new(initial.Request.Targets) { DispatchPhase = number => number switch { 1 => EUpdatePhase.Interrupted, _ => EUpdatePhase.Completed } };
        TaskCompletionSource filled = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource unknown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatched = 0;
        string firstCluster = "0", unconfirmed = "Unconfirmed";
        remote.AfterDispatch = async (target, token) =>
        {
            if (Interlocked.Increment(ref dispatched) == 3)
            {
                filled.TrySetResult();
            }
            await filled.Task.WaitAsync(token).ConfigureAwait(false);
            if (!string.Equals(target.ClusterId, firstCluster, StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<FleetUpdateStatus> run = new FleetUpdateRunner(remote).RunAsync(initial, (status, _) =>
        {
            if (status.Peers.Any(peer => string.Equals(peer.State, unconfirmed, StringComparison.Ordinal)))
            {
                unknown.TrySetResult();
            }
            return Task.CompletedTask;
        }, timeout.Token);
        try
        {
            await unknown.Task.WaitAsync(timeout.Token);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            filled.TrySetResult();
            release.TrySetResult();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        FleetUpdateStatus result = await run.ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal(2, result.Peers.Count(peer => peer.State is "Completed"));
        Assert.All(result.Peers.Skip(3), peer => Assert.Empty(peer.Attempts));
        Assert.Equal(3, dispatched);
    }

    /// <summary>発見されていない所属ピアを、過半数の分母から除外しません。</summary>
    /// <returns>見かけ上2ピアでも3ピア構成を守る保留です。</returns>
    [Fact]
    public async Task OfflineMemberMissingFromThePlanStillCountsTowardQuorum()
    {
        FleetUpdateStatus initial = Status([Target("a", "one"), Target("b", "one")], 3);
        FleetUpdateTestRemote remote = new(initial.Request.Targets);
        remote.Observations[("one", "offline")] = new(Target("offline", "one").Peer, false, "old");
        FleetUpdateStatus result = await new FleetUpdateRunner(remote).RunAsync(initial, (_, _) => Task.CompletedTask).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.All(result.Peers, peer => Assert.Equal("Deferred", peer.State));
        Assert.Empty(remote.Events);
    }

    /// <summary>全ピアが同じPCでも、3ピア以上ならReady過半数の条件を省略しません。</summary>
    /// <returns>同一PCの小規模例外が広がらない確認です。</returns>
    [Fact]
    public async Task ThreeMembersOnOnePcStillRequireAReadyMajority()
    {
        FleetUpdateTarget[] targets = new[] { Target("a", "one"), Target("b", "one"), Target("c", "one") }
            .Select(target => target with { Peer = target.Peer with { HostId = "same-pc" } }).ToArray();
        FleetUpdateStatus initial = Status(targets, 3);
        FleetUpdateTestRemote remote = new(initial.Request.Targets);
        remote.Observations[("one", "c")] = remote.Observations[("one", "c")] with { Ready = false };
        FleetUpdateStatus result = await new FleetUpdateRunner(remote).RunAsync(initial, (_, _) => Task.CompletedTask).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(remote.Events);
    }

    /// <summary>上限を迂回する直接呼出しでも副作用前に入力を拒否します。</summary>
    /// <returns>開始されなかった更新です。</returns>
    [Theory, InlineData(0), InlineData(4)]
    public async Task InvalidConcurrencyIsRejected(int limit)
    {
        FleetUpdateStatus initial = Status([Target("a", "one")], limit);
        FleetUpdateTestRemote remote = new(initial.Request.Targets);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FleetUpdateRunner(remote).RunAsync(initial, (_, _) => Task.CompletedTask));
        Assert.Empty(remote.Events);
        Assert.Equal(1, new FleetReleaseSelection(initial.Request.Release.Manifest, Path.GetTempPath()).MaximumParallelism);
    }

    /// <summary>試行と並列の上限を持つ一回の固定要求を作ります。</summary>
    /// <returns>未着手の更新状態です。</returns>
    private static FleetUpdateStatus Status(FleetUpdateTarget[] targets, int limit)
    {
        ReleaseManifest manifest = new("new", RuntimeInformation.RuntimeIdentifier, "app", [new("app", 0, new string('0', 64))], ["old"], "new");
        FleetUpdateRequest request = new(Guid.NewGuid(), new(manifest, Path.GetTempPath(), true, PeerTimeoutMilliseconds: 1000, MaximumParallelism: limit), targets);
        return new(request, false, EOutcome.Unknown, targets.Select(target => new FleetUpdatePeerProgress(target, "Queued", [])).ToArray(), DateTimeOffset.UtcNow);
    }

    /// <summary>独立したPC・クラスタのピアを構成します。</summary>
    /// <returns>開始時に固定する実体です。</returns>
    private static FleetUpdateTarget Target(string host, string cluster) => new(cluster,
        new(host, Guid.NewGuid(), host, "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow),
        new("loopback", "127.0.0.1", "127.255.255.255", 1234));
}
