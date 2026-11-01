using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>独立したPeerへ並行配送しても、prepare/commit/abortの境界を崩しません。</summary>
public sealed class ParallelReplicationTests
{
    /// <summary>遅い準備も開始済みなら待ち、全準備成功後だけコミットし、拒否時は遅着予約も中止します。</summary>
    /// <returns>実UDPの並行準備、段階境界と後着予約の解放です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllStartedPreparationsFinishBeforeCommitOrAbort(bool reject)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        PausedRepository local = new(), left = new() { Reject = reject }, right = new();
        await using PeerCluster first = Create(local, cluster, "first", port, true);
        await using PeerCluster second = Create(left, cluster, "second", port, false);
        await using PeerCluster third = Create(right, cluster, "third", port, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        await third.StartAsync(timeout.Token);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 3)
        {
            await Task.Delay(20, timeout.Token);
        }
        Guid operation = Guid.NewGuid();
        left.PausedOperation = right.PausedOperation = operation;
        Task<DurableReceipt> writing = first.ReplicateAsync(operation, [new("configuration", "parallel", null, NodeValue.FromObject(42))], timeout.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable writingLifetime = new TestTaskLifetime(writing, timeout).ConfigureAwait(true);
        try
        {
            await Task.WhenAll(left.Entered.Task, right.Entered.Task).WaitAsync(timeout.Token);
            left.Release.TrySetResult();
            await left.Finished.Task.WaitAsync(timeout.Token);
            Assert.False(writing.IsCompleted);
            Assert.Equal(0, local.Commits + left.Commits + right.Commits);
            Assert.Equal(0, local.Aborts + left.Aborts + right.Aborts);
            right.Release.TrySetResult();
            DurableReceipt result = await writing.WaitAsync(timeout.Token);
            if (reject)
            {
                Assert.Equal(EOutcome.Failed, result.Outcome);
                Assert.Equal(0, local.Commits + left.Commits + right.Commits);
                Assert.False(await local.HasPendingWriteAsync("configuration", "parallel", timeout.Token));
                Assert.False(await right.HasPendingWriteAsync("configuration", "parallel", timeout.Token));
                Assert.Null(await right.ReadAsync("configuration", "parallel", timeout.Token));
            }
            else
            {
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                Assert.Equal(3, local.Commits + left.Commits + right.Commits);
                Assert.Equal(42, (await right.ReadAsync("configuration", "parallel", timeout.Token))!.Value!.Json.GetInt32());
            }
        }
        finally
        {
            left.Release.TrySetResult();
            right.Release.TrySetResult();
        }
    }

    /// <summary>参照専用照会も使用する通常の実通信Peerです。</summary>
    /// <returns>隔離した発見範囲のPeerです。</returns>
    private static PeerCluster Create(IDurableRepository repository, string cluster, string peer, int port, bool fresh) => new(
        new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath())
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromSeconds(10) }, repository)
    { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>実際の保存規則を使い、指定操作の準備確認だけを止めます。</summary>
    private sealed class PausedRepository : DurableRepository
    {
        private readonly Lock gate = new();
        private readonly RepositoryDocument document = new();

        internal Guid PausedOperation
        {
            get; set;
        }

        internal bool Reject
        {
            get; init;
        }

        internal int Commits
        {
            get; private set;
        }

        internal int Aborts
        {
            get; private set;
        }

        internal TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>一Peerの準備待ちで他Peerへの配送が止まらないことを確認します。</summary>
        /// <returns>通常準備または明示した拒否です。</returns>
        public override async ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
        {
            if (operationId == PausedOperation)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (Reject)
                {
                    Finished.TrySetResult();
                    return new(operationId, string.Empty, EOutcome.Failed, [], "fixture.rejected");
                }
            }
            DurableReceipt result = await base.PrepareAsync(operationId, mutations, cancellationToken).ConfigureAwait(false);
            Finished.TrySetResult();
            return result;
        }

        /// <summary>全準備が終わる前にコミットへ進んでいないことを数えます。</summary>
        /// <returns>元の保存結果です。</returns>
        public override ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            Commits++;
            return base.CommitAsync(operationId, cancellationToken);
        }

        /// <summary>準備応答が遅いPeerも取り残さず中止します。</summary>
        /// <returns>予約解除結果です。</returns>
        public override ValueTask<DurableReceipt> AbortPreparedAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            Aborts++;
            return base.AbortPreparedAsync(operationId, cancellationToken);
        }

        /// <summary>一回の保存を試験文書上でも排他します。</summary>
        /// <returns>製品の保存規則による結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
