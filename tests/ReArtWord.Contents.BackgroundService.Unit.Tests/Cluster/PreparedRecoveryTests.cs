using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>永続準備を持って再起動した複数Peerが、保存判断だけで安全に復旧することを検証します。</summary>
public sealed class PreparedRecoveryTests
{
    /// <summary>準備途中、確定決定後、局所確定後のいずれでも、元操作だけを全参加者で終了します。</summary>
    /// <returns>実通信による再参加、予約解放、元結果の再照会と再適用防止です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartedParticipantsFinishThePersistedDecisionAsync(bool commit, bool localCommitted)
    {
        using MediaTestFiles files = new();
        string firstPath = Path.Combine(files.Root, "first.db"), secondPath = Path.Combine(files.Root, "second.db");
        SqliteRepository firstStore = new(firstPath), secondStore = new(secondPath);
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        PeerInfo[] original = await InitializeMembersAsync(firstStore, secondStore, files.Root, cluster, port);
        Guid operation = Guid.NewGuid();
        IReadOnlyList<DurableMutation> changes = await firstStore.StampAsync([new("workflow:recovery", "value", null, NodeValue.FromObject(42))]);
        string fingerprint = (await firstStore.PrepareAsync(operation, changes)).Fingerprint;
        Assert.Equal(EOutcome.Succeeded, (await secondStore.PrepareAsync(operation, changes)).Outcome);
        string phase = ReplicationJournal.Preparing;
        if (commit)
        {
            phase = ReplicationJournal.CommitDecided;
        }
        DurableReceipt pending = new(operation, fingerprint, EOutcome.Unknown, [], phase);
        Assert.Equal(EOutcome.Succeeded, (await firstStore.ApplyBatchAsync(Guid.NewGuid(), [
            new("cluster:operations", operation.ToString("N"), null, NodeValue.FromObject(pending)),
            new("cluster:replication-members", operation.ToString("N"), null, NodeValue.FromObject(original)),
        ])).Outcome);
        if (localCommitted)
        {
            Assert.Equal(EOutcome.Succeeded, (await firstStore.CommitAsync(operation)).Outcome);
        }
        firstStore = new(firstPath);
        secondStore = new(secondPath);
        PeerCluster first = Create(firstStore, files.Root, cluster, "first", port, true);
        await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        PeerCluster second = Create(secondStore, files.Root, cluster, "second", port, false);
        await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        EOutcome expected = commit ? EOutcome.Succeeded
                                   : EOutcome.Failed;
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2
            || await firstStore.ReadPreparationAsync(operation, timeout.Token) is not null
            || await secondStore.ReadPreparationAsync(operation, timeout.Token) is not null
            || (await first.GetOperationAsync(operation, timeout.Token))?.Outcome != expected)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal(expected, (await firstStore.ReadReceiptAsync(operation, timeout.Token))!.Outcome);
        Assert.Equal(expected, (await secondStore.ReadReceiptAsync(operation, timeout.Token))!.Outcome);
        Assert.Equal(expected, (await first.ReplicateAsync(operation, changes, timeout.Token)).Outcome);
        Assert.Equal(expected, (await secondStore.CommitAsync(operation, timeout.Token)).Outcome);
        Assert.Equal(await firstStore.GetSharedVersionAsync(timeout.Token), await secondStore.GetSharedVersionAsync(timeout.Token));
        DurableEntry? value = await secondStore.ReadAsync("workflow:recovery", "value", timeout.Token);
        if (commit)
        {
            Assert.Equal(42, value!.Value!.Json.GetInt32());
        }
        else
        {
            Assert.Null(value);
        }
    }

    /// <summary>由来不明の旧準備は、時間が過ぎたことだけで破棄しません。</summary>
    /// <returns>元予約を維持し、確認不能な理由を公開することです。</returns>
    [Fact]
    public async Task MissingDecisionIsDiagnosedWithoutGuessingAnAbortAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        Guid id = Guid.NewGuid();
        await repository.PrepareAsync(id, [new("workflow:legacy", "value", null, NodeValue.FromObject(1))]);
        PeerCluster peer = Create(repository, files.Root, Guid.NewGuid().ToString("N"), "first", LargeSynchronizationTests.Port(), true);
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        await peer.StartAsync(timeout.Token);
        while (peer.SynchronizationProblem is null)
        {
            await Task.Delay(30, timeout.Token);
        }
        Assert.Equal("Replication.PreparedDecisionUnconfirmed", peer.SynchronizationProblem);
        Assert.NotNull(await repository.ReadPreparationAsync(id, timeout.Token));
        Assert.Null(await repository.ReadReceiptAsync(id, timeout.Token));
        Assert.Null(await repository.ReadAsync("workflow:legacy", "value", timeout.Token));
    }

    /// <summary>実参加で保存所有identityと信頼済み復旧点を作り、停止後のDBへ障害位置を準備します。</summary>
    /// <returns>停止前に確認した固定参加者です。</returns>
    private static async Task<PeerInfo[]> InitializeMembersAsync(SqliteRepository source, SqliteRepository target, string root, string cluster, int port, CancellationToken cancellationToken = default)
    {
        PeerCluster first = Create(source, root, cluster, "first", port, true);
        await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        PeerCluster second = Create(target, root, cluster, "second", port, false);
        await using ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
        await first.StartAsync(linked.Token).ConfigureAwait(false);
        await second.StartAsync(linked.Token).ConfigureAwait(false);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, linked.Token).ConfigureAwait(false);
        }
        return first.GetPeers().ToArray();
    }

    /// <summary>隔離したループバック通信と短い確認周期で再参加します。</summary>
    /// <returns>本番通信を使用しないPeerです。</returns>
    private static PeerCluster Create(SqliteRepository repository, string root, string cluster, string peer, int port, bool fresh)
        => new(new ClusterOptions(cluster, peer, "host-" + peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, root)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromMilliseconds(400), RequestTimeout = TimeSpan.FromMilliseconds(400) }, repository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };
}
