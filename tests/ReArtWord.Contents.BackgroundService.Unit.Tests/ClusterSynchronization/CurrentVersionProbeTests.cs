using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>ハートビートの古い版と、両端で既に一致した現在値を区別します。</summary>
public sealed class CurrentVersionProbeTests
{
    /// <summary>照会開始時の版が相手と一致した後の更新を、新しい内容分岐と取り違えません。</summary>
    /// <returns>連続書込み中の一致点を実UDPで確認し、永続不一致だけは同期対象にする検証です。</returns>
    [Fact]
    public async Task MatchingVersionBeforeLocalCommitAvoidsHistoryTransferAsync()
    {
        using MediaTestFiles files = new();
        SyncRecoveryPeer peer = new(new([], []));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await peer.StartAsync(deadline.Token).ConfigureAwait(true);
        await peer.EstablishAsync(deadline.Token).ConfigureAwait(true);
        PeerInfo local = Assert.Single(peer.Target.GetPeers(), item => item.InstanceId == peer.Target.LocalInstanceId);
        PeerInfo remote = Assert.Single(peer.Target.GetPeers(), item => item.InstanceId != peer.Target.LocalInstanceId);
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, files.Root);
        RepositorySynchronization inspector = new(peer.Target, peer.Repository, options, TimeProvider.System,
            () => throw new InvalidOperationException("No export is expected."), CancellationToken.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable inspectorLifetime = inspector.ConfigureAwait(true);
        peer.ReadVersion = async token =>
        {
            string matched = await peer.Repository.GetSharedVersionAsync(token).ConfigureAwait(false);
            string key = peer.VersionProbes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(EOutcome.Succeeded, (await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:progress", key, null, NodeValue.FromObject(42))], token).ConfigureAwait(false)).Outcome);
            return matched;
        };
        for (int index = 0; index < 3; index++)
        {
            string before = await peer.Repository.GetSharedVersionAsync(deadline.Token).ConfigureAwait(true);
            SyncVersionComparison compared = await inspector.CompareVersionAsync(remote, deadline.Token).ConfigureAwait(true);
            Assert.Equal(before, compared.MatchingVersion);
            Assert.False(compared.RequiresSynchronization);
            Assert.NotEqual(before, await peer.Repository.GetSharedVersionAsync(deadline.Token).ConfigureAwait(true));
            Assert.Equal(index + 1, peer.VersionProbes);
        }
        peer.ReadVersion = _ => Task.FromResult("permanent-difference");
        Assert.True((await inspector.CompareVersionAsync(remote, deadline.Token).ConfigureAwait(true)).RequiresSynchronization);
        Assert.Equal(1, peer.Opens);
        Assert.Equal(2, peer.Target.GetPeers().Count(item => item.Status == EPeerStatus.Ready));
    }

    /// <summary>比較中のローカル変更と相手側の変更を、固定した内容分岐と取り違えません。</summary>
    /// <returns>実UDP上の再照合と、一致後に履歴を転送しない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingVersionsWaitForAComparableObservationAsync(bool localChanges)
    {
        SyncRecoveryPeer peer = new(new([], []));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await peer.StartAsync(deadline.Token).ConfigureAwait(true);
        await peer.EstablishAsync(deadline.Token).ConfigureAwait(true);
        peer.ReadVersion = async token =>
        {
            string version = "remote-" + peer.VersionProbes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (localChanges)
            {
                version = await peer.Repository.GetSharedVersionAsync(token).ConfigureAwait(false);
                string key = peer.VersionProbes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(EOutcome.Succeeded, (await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:changing", key, null, NodeValue.FromObject(42))], token).ConfigureAwait(false)).Outcome);
            }
            return version;
        };
        await peer.AnnounceUntilVersionProbeAsync("changing", deadline.Token).ConfigureAwait(true);
        int probes = 2;
        if (localChanges)
        {
            probes = 1;
        }
        while (peer.VersionProbes < probes)
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
        await Task.Delay(150, deadline.Token).ConfigureAwait(true);
        Assert.Equal(1, peer.Opens);
        Assert.Null(peer.Target.SynchronizationProblem);
        peer.ReadVersion = token => peer.Repository.GetSharedVersionAsync(token).AsTask();
        await peer.AnnounceUntilVersionProbeAsync("last-commit", deadline.Token).ConfigureAwait(true);
        while (peer.VersionProbes < probes + 1)
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
        await Task.Delay(150, deadline.Token).ConfigureAwait(true);
        Assert.Equal(1, peer.Opens);
        Assert.Equal(2, peer.Target.GetPeers().Count(item => item.Status == EPeerStatus.Ready));
    }

    /// <summary>一時的な複製途中だけを待ち、差が残る場合は有限時間で実内容を取り込みます。</summary>
    /// <returns>実UDPの再照合と、不要な履歴転送の抑制です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyPeersRecheckTransientDifferencesBeforeTransferringHistory(bool persistent)
    {
        RepositorySnapshot snapshot = new([new("workflow:settle", "value", "changed", NodeValue.FromObject(42))], []);
        SyncRecoveryPeer peer = new(new([], []));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await peer.StartAsync(deadline.Token).ConfigureAwait(true);
        await peer.EstablishAsync(deadline.Token).ConfigureAwait(true);
        peer.SetSnapshot(snapshot);
        Assert.Equal(1, peer.Opens);
        peer.ReadVersion = token =>
        {
            if (persistent || peer.VersionProbes == 1)
            {
                return Task.FromResult("between-commits");
            }
            return peer.Repository.GetSharedVersionAsync(token).AsTask();
        };
        await peer.AnnounceUntilVersionProbeAsync("between-commits", deadline.Token).ConfigureAwait(true);
        while (peer.VersionProbes < 2)
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
        if (persistent)
        {
            DurableEntry? imported;
            do
            {
                await Task.Delay(10, deadline.Token).ConfigureAwait(true);
                imported = await peer.Repository.ReadAsync("workflow:settle", "value", deadline.Token).ConfigureAwait(true);
            }
            while (imported is null);
            Assert.Equal(42, imported.Value!.Json.GetInt32());
            Assert.Equal(2, peer.Opens);
        }
        else
        {
            await Task.Delay(200, deadline.Token).ConfigureAwait(true);
            Assert.Equal(1, peer.Opens);
            Assert.Null(await peer.Repository.ReadAsync("workflow:settle", "value", deadline.Token).ConfigureAwait(true));
        }
        Assert.Equal(2, peer.VersionProbes);
        Assert.Null(peer.Target.SynchronizationProblem);
    }

    /// <summary>初めて参加した相手の差分は、通常参加中の一時差分確認で遅らせません。</summary>
    /// <returns>再照合待ちを挟まない最初の実同期です。</returns>
    [Fact]
    public async Task NewPeerDifferenceStartsSynchronizationWithoutWaitingForAnotherProbe()
    {
        RepositorySnapshot snapshot = new([new("workflow:join", "value", "initial", NodeValue.FromObject(42))], []);
        SyncRecoveryPeer peer = new(snapshot) { ReadVersion = _ => throw new InvalidOperationException("New peers require synchronization.") };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await peer.StartAsync(deadline.Token).ConfigureAwait(true);
        await peer.AnnounceAsync("initial", cancellationToken: deadline.Token).ConfigureAwait(true);
        DurableEntry? imported;
        do
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
            imported = await peer.Repository.ReadAsync("workflow:join", "value", deadline.Token).ConfigureAwait(true);
        }
        while (imported is null);
        Assert.Equal(42, imported.Value!.Json.GetInt32());
        Assert.Equal(1, peer.Opens);
        Assert.Equal(0, peer.VersionProbes);
    }

    /// <summary>相手の版が変化し続けても、有限の猶予後には実データを確認します。</summary>
    /// <returns>既存の差分を無期限に隠さない実UDPの同期です。</returns>
    [Fact]
    public async Task ContinuousChangesCannotPostponeRealSynchronizationIndefinitelyAsync()
    {
        SyncRecoveryPeer peer = new(new([], []));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        await peer.StartAsync(deadline.Token).ConfigureAwait(true);
        await peer.EstablishAsync(deadline.Token).ConfigureAwait(true);
        peer.SetSnapshot(new([new("workflow:required", "value", "remote", NodeValue.FromObject(42))], []));
        peer.ReadVersion = _ => Task.FromResult("changing-" + peer.VersionProbes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        DurableEntry? imported;
        do
        {
            await peer.AnnounceAsync("changing", cancellationToken: deadline.Token).ConfigureAwait(true);
            await Task.Delay(100, deadline.Token).ConfigureAwait(true);
            imported = await peer.Repository.ReadAsync("workflow:required", "value", deadline.Token).ConfigureAwait(true);
        }
        while (imported is null);
        Assert.Equal(42, imported.Value!.Json.GetInt32());
        Assert.Equal(2, peer.Opens);
        Assert.True(2 < peer.VersionProbes);
    }

    /// <summary>実UDPの小さい照会だけで一致を確認し、不一致を同期済みとして扱いません。</summary>
    /// <returns>値・操作履歴を変えない確認結果です。</returns>
    [Fact]
    public async Task CurrentVersionIsComparedWithoutCreatingOperationHistory()
    {
        using MediaTestFiles files = new();
        string id = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        SqliteRepository source = new(Path.Combine(files.Root, "a.db"));
        SqliteRepository destination = new(Path.Combine(files.Root, "b.db"));
        await using PeerCluster first = LargeSynchronizationTests.Create(source, files.Root, id, "a", port, true);
        await using PeerCluster second = LargeSynchronizationTests.Create(destination, files.Root, id, "b", port, false);
        await first.StartAsync();
        await second.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, deadline.Token);
        }
        Assert.Equal(EOutcome.Succeeded, (await first.ReplicateAsync(Guid.NewGuid(), [new("workflow:probe", "value", null, NodeValue.FromObject(42))], deadline.Token)).Outcome);
        PeerInfo remote = Assert.Single(first.GetPeers(), peer => peer.PeerId is "b");
        ClusterOptions options = new(id, "a", "a", "127.0.0.1", "127.255.255.255", port, 1, true, files.Root);
        RepositorySynchronization inspector = new(first, source, options, TimeProvider.System, () => throw new InvalidOperationException("No export is expected."), CancellationToken.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable inspectorLifetime = inspector.ConfigureAwait(true);
        RepositorySnapshot before = await source.ExportAsync(deadline.Token);
        string version = await source.GetSharedVersionAsync(deadline.Token);
        Assert.Equal(new SyncVersionComparison(version), await inspector.CompareVersionAsync(remote, deadline.Token));
        Assert.Equal(before.Receipts.Select(receipt => receipt.OperationId).Order(), (await source.ExportAsync(deadline.Token)).Receipts.Select(receipt => receipt.OperationId).Order());
        SqliteRepository different = new(Path.Combine(files.Root, "different.db"));
        RepositorySynchronization mismatched = new(first, different, options, TimeProvider.System, () => throw new InvalidOperationException("No export is expected."), CancellationToken.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable mismatchLifetime = mismatched.ConfigureAwait(true);
        Assert.Equal(new SyncVersionComparison(), await mismatched.CompareVersionAsync(remote, deadline.Token));
        Assert.Equal(version, await destination.GetSharedVersionAsync(deadline.Token));
    }
}
