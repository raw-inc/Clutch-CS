using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>解放をまとめても所有者、固定参加者と保持中の排他を変えないことを確認します。</summary>
[Collection("Performance")]
public sealed class FileReservationReleaseTests
{
    /// <summary>調停待ちの解放を一度で確定し、所有情報が異なる要求だけを拒否します。</summary>
    /// <returns>別パスの保持予約と、不正な解放対象を残した台帳です。</returns>
    [Fact]
    public async Task QueuedReleasesKeepOwnershipAndOtherReservationsAsync()
    {
        DelayedGateRepository repository = new();
        await using PeerCluster cluster = Cluster(repository);
        using FileReservationCoordinator coordinator = new(cluster, repository);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await cluster.StartAsync(timeout.Token);
        PeerInfo peer = Assert.Single(cluster.GetPeers());
        FileLease[] leases = Enumerable.Range(0, 12).Select(index => Lease(peer, "/held/" + index)).ToArray();
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "active", null,
            NodeValue.FromObject(leases.ToDictionary(lease => lease.Id)))], timeout.Token);
        repository.Pause();
        FileLease barrier = Lease(peer, "/barrier");
        Task<ClusterResponse> acquiring = coordinator.AcquireAsync(Request(peer, "file.gate.acquire", barrier), timeout.Token);
        List<Task> running = [acquiring];
        Task<ClusterResponse>[] releasing;
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token);
            releasing = leases.Select((lease, index) => coordinator.ReleaseAsync(Request(peer, "file.gate.release",
                new FileGateRelease(index == 0 ? lease with { OwnerInstanceId = Guid.NewGuid() }
                                               : lease, [peer])), timeout.Token)).ToArray();
            running.AddRange(releasing);
            Assert.All(releasing, operation => Assert.False(operation.IsCompleted));
        }
        finally
        {
            repository.Resume.TrySetResult();
            await Task.WhenAll(running).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await acquiring.ConfigureAwait(true)).Outcome);
        ClusterResponse[] results = await Task.WhenAll(releasing);
        Assert.Equal("File.LeaseOwnerMismatch", results[0].Reason);
        Assert.All(results.Skip(1), result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        Assert.Equal(2, repository.Preparations);
        FileReservationLedger ledger = await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token);
        Assert.Equal(2, ledger.Active.Count);
        Assert.Contains(leases[0].Id, ledger.Active.Keys);
        Assert.Contains(barrier.Id, ledger.Active.Keys);
    }

    /// <summary>確認不能な参加者を持つ解放を、確認済みの集合へ縮小して処理しません。</summary>
    /// <returns>正常な集合だけを解放し、未確認の元予約を残します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentMembershipsAreNeverCombinedAsync(bool storageFailure)
    {
        int? failedPreparation = null;
        if (storageFailure)
        {
            failedPreparation = 3;
        }
        DelayedGateRepository repository = new() { ThrowAtPreparation = failedPreparation };
        await using PeerCluster cluster = Cluster(repository);
        using FileReservationCoordinator coordinator = new(cluster, repository);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await cluster.StartAsync(timeout.Token);
        PeerInfo peer = Assert.Single(cluster.GetPeers());
        FileLease first = Lease(peer, "/first"), second = Lease(peer, "/second");
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "active", null,
            NodeValue.FromObject(new Dictionary<Guid, FileLease> { [first.Id] = first, [second.Id] = second }))], timeout.Token);
        repository.Pause();
        FileLease barrier = Lease(peer, "/barrier");
        Task<ClusterResponse> acquiring = coordinator.AcquireAsync(Request(peer, "file.gate.acquire", barrier), timeout.Token);
        List<Task> running = [acquiring];
        Task<ClusterResponse> released, unconfirmed;
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token);
            released = coordinator.ReleaseAsync(Request(peer, "file.gate.release", new FileGateRelease(first, [peer])), timeout.Token);
            running.Add(released);
            PeerInfo missing = peer with { PeerId = "missing", InstanceId = Guid.NewGuid(), HostId = "other" };
            unconfirmed = coordinator.ReleaseAsync(Request(peer, "file.gate.release", new FileGateRelease(second, [peer, missing])), timeout.Token);
            running.Add(unconfirmed);
        }
        finally
        {
            repository.Resume.TrySetResult();
            await Task.WhenAll(running).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await acquiring.ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await released.ConfigureAwait(true)).Outcome);
        Assert.NotEqual(EOutcome.Succeeded, (await unconfirmed.ConfigureAwait(true)).Outcome);
        FileReservationLedger ledger = await FileReservationLedger.ReadAsync(repository, "file:reservations", timeout.Token);
        Assert.DoesNotContain(first.Id, ledger.Active.Keys);
        Assert.Contains(second.Id, ledger.Active.Keys);
        Assert.Contains(barrier.Id, ledger.Active.Keys);
    }

    /// <summary>実UDPと製品の保存規則を使い、外部PCへ接続しません。</summary>
    /// <returns>呼出し元が停止する参加実体です。</returns>
    private static PeerCluster Cluster(DelayedGateRepository repository) => new(new(Guid.NewGuid().ToString("N"), "peer", "host",
        "127.0.0.1", "127.255.255.255", LargeSynchronizationTests.Port(), 1, true, Path.GetTempPath())
    { HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromMilliseconds(250) }, repository)
    { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>所有者と対象パスを固定した予約です。</summary>
    /// <returns>ほかの試験のパスと競合しない予約です。</returns>
    private static FileLease Lease(PeerInfo peer, string path) => new(Guid.NewGuid(), peer.PeerId, peer.InstanceId, peer, [peer],
        [new(peer.PeerId, peer.InstanceId, peer.HostId, path, true)]) { ReservationId = Guid.NewGuid() };

    /// <summary>一つの操作識別子を持つ調停要求を作成します。</summary>
    /// <returns>同じ実体に宛てた要求です。</returns>
    private static ClusterRequest Request(PeerInfo peer, string kind, object body) => new(Guid.NewGuid(), kind, peer.PeerId,
        peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(body));
}
