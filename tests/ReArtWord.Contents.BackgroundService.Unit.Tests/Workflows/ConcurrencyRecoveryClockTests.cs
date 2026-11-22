using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>並列枠の所有照会が日時補正で止まらず、未確認の所有者を解放しないことを確認します。</summary>
public sealed class ConcurrencyRecoveryClockTests
{
    /// <summary>同じ5秒間隔で所有者を照会し、終了が確認できた枠だけを解放します。</summary>
    /// <returns>実Peer・SQLiteと、手動で進める待機タイマーの検証です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task OwnershipPollingSurvivesClockCorrectionsWithoutReleasingUnknownOwnersAsync(int correction)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        PeerCluster peer = new(new ClusterOptions(Guid.NewGuid().ToString("N"), "local", "host", "127.0.0.1", "127.255.255.255", LargeSynchronizationTests.Port(), 1, true, files.Root), repository)
        { ReadinessCheck = token => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await peer.StartAsync(timeout.Token);
        IterationTestClock clock = new();
        WorkflowConcurrencyRequest first = new(Guid.CreateVersion7(), "flow", "limit", Guid.NewGuid(), peer.LocalPeerId, peer.LocalInstanceId, 1, 0, false);
        WorkflowConcurrencyRequest second = first with { TicketId = Guid.CreateVersion7(), RunId = Guid.NewGuid() };
        ConcurrentQueue<WorkflowConcurrencyRequest[]> calls = new();
        int phase = 0;
        Task<IReadOnlyDictionary<Guid, bool>> OwnersAsync(IReadOnlyList<WorkflowConcurrencyRequest> requests, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Enqueue(requests.ToArray());
            IReadOnlyDictionary<Guid, bool> reply = Volatile.Read(ref phase) switch
            {
                0 => requests.ToDictionary(request => request.TicketId, request => true),
                1 => new Dictionary<Guid, bool>(),
                _ => requests.ToDictionary(request => request.TicketId, request => request.TicketId != first.TicketId),
            };
            return Task.FromResult(reply);
        }
        ConcurrencyAuthority authority = new(peer, repository, OwnersAsync, clock);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable authorityLifetime = authority.ConfigureAwait(false);
        ClusterRequest Command(WorkflowConcurrencyRequest request) => new(request.TicketId, "concurrency.acquire", peer.LocalPeerId, peer.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(request));
        Assert.Equal(EOutcome.Succeeded, (await authority.AcquireAsync(Command(first), timeout.Token)).Outcome);
        Task<ClusterResponse> waiting = authority.AcquireAsync(Command(second), timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable waitingLifetime = new TestTaskLifetime(waiting, timeout).ConfigureAwait(false);
        await WaitForTimerAsync(clock, timeout.Token);
        Assert.Equal(2, calls.Count);
        clock.Adjust(TimeSpan.FromSeconds(correction));
        Volatile.Write(ref phase, 1);
        for (int secondIndex = 0; secondIndex < 4; secondIndex++)
        {
            clock.Advance(1000);
            await WaitForTimerAsync(clock, timeout.Token);
            Assert.Equal(2, calls.Count);
            Assert.False(waiting.IsCompleted);
        }
        clock.Advance(1000);
        await WaitForTimerAsync(clock, timeout.Token);
        Assert.Equal(3, calls.Count);
        Assert.Equal(2, calls.Last().Length);
        Assert.False(waiting.IsCompleted);
        Volatile.Write(ref phase, 2);
        clock.Advance(5000);
        Assert.Equal(EOutcome.Succeeded, (await waiting.WaitAsync(timeout.Token)).Outcome);
        Assert.Equal(4, calls.Count);
        Assert.Equal(2, calls.Last().Length);
    }

    /// <summary>待機処理がtimerを登録し終えたことを確認してから、次の経過時間を与えます。</summary>
    /// <returns>次の待機の開始です。30秒の外側期限は試験の停止不能を検出するためです。</returns>
    private static async Task WaitForTimerAsync(IterationTestClock clock, CancellationToken cancellationToken = default)
    {
        while (clock.Pending == 0)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
