using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>保存準備の再照会と診断の待機を、UTC補正や予約の取消と区別します。</summary>
public sealed class ReplicationRecoveryClockTests
{
    /// <summary>時刻補正では追加I/Oや取消を行わず、実際の間隔が過ぎたら確認と診断を続けます。</summary>
    /// <returns>実保存規則による未確定準備の保持です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task ClockCorrectionDoesNotChangeRetryOrGuessAStoredDecisionAsync(int correction)
    {
        CountingRepository repository = new();
        CollaborationClock clock = new();
        Guid operation = Guid.CreateVersion7();
        await repository.PrepareAsync(operation, [new("workflow:pending", "value", null, NodeValue.FromObject(1))]);
        PeerCluster peer = new(new ClusterOptions("clock-recovery", "local", "host", "127.0.0.1", "127.255.255.255", 12345, 1, false, Path.GetTempPath()), repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        using SemaphoreSlim gate = new(1, 1);
        ReplicationRecovery recovery = new(peer, repository, new ReplicationJournal(repository), gate, clock);
        await recovery.RefreshAsync();
        int first = repository.Reads;
        Assert.True(0 < first);
        clock.Adjust(TimeSpan.FromSeconds(correction));
        await recovery.RefreshAsync();
        Assert.Equal(first, repository.Reads);
        Assert.Null(recovery.Problem);
        clock.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        await recovery.RefreshAsync();
        Assert.Equal(first, repository.Reads);
        clock.Advance(TimeSpan.FromTicks(1));
        await recovery.RefreshAsync();
        Assert.True(first < repository.Reads);
        Assert.Null(recovery.Problem);
        clock.Advance(TimeSpan.FromSeconds(4));
        await recovery.RefreshAsync();
        Assert.Equal("Replication.PreparedDecisionUnconfirmed", recovery.Problem);
        clock.Advance(TimeSpan.FromDays(1));
        await recovery.RefreshAsync();
        Assert.NotNull(await repository.ReadPreparationAsync(operation));
        Assert.Null(await repository.ReadReceiptAsync(operation));
        Assert.Null(await repository.ReadAsync("workflow:pending", "value"));
    }

    /// <summary>製品の保存規則を使い、再確認間隔中に保存先を読み続けていないか数えます。</summary>
    private sealed class CountingRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();

        internal int Reads
        {
            get; private set;
        }

        /// <summary>保存処理を直列化し、読取りだけを数えます。</summary>
        /// <returns>通常の保存規則による結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                if (!write)
                {
                    Reads++;
                }
                return operation(document);
            }
        }
    }
}
