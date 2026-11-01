using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>保存完了と結果の再確認が並行しても、同じ確定結果を失敗にしないことを検証します。</summary>
public sealed class ReplicationConfirmationTests
{
    /// <summary>再確認が先に同じ成功を保存した場合も、元の変更は成功として返ります。</summary>
    /// <returns>実複製処理と、同じ操作の確認が競合した結果です。</returns>
    [Fact]
    public async Task ConcurrentConfirmationPreservesTheSuccessfulOriginalWriteAsync()
    {
        ConfirmationRepository repository = new();
        await using PeerCluster peer = Create(repository);
        await peer.StartAsync().ConfigureAwait(true);
        repository.PausedOperation = Guid.NewGuid();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<DurableReceipt> writing = peer.ReplicateAsync(repository.PausedOperation,
            [new("workflow:confirmation", "value", null, NodeValue.FromObject(42))], timeout.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable writingLifetime = new TestTaskLifetime(writing, timeout).ConfigureAwait(true);
        try
        {
            await repository.ReadCaptured.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            DurableReceipt? confirmed = await peer.ConfirmReplicationAsync(repository.PausedOperation, peer.GetPeers(), timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, confirmed?.Outcome);
        }
        finally
        {
            repository.ContinueWrite.TrySetResult();
        }
        DurableReceipt original = await writing.ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, original.Outcome);
        Assert.Equal(42, (await repository.ReadAsync("workflow:confirmation", "value", timeout.Token).ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(1, repository.Commits);
    }

    /// <summary>別内容の確認結果が競合した場合は、元の成功と取り違えません。</summary>
    /// <returns>異なる結果を上書きしないことの確認です。</returns>
    [Fact]
    public async Task DifferentConcurrentResultRemainsUnconfirmedAsync()
    {
        ConfirmationRepository repository = new();
        await using PeerCluster peer = Create(repository);
        await peer.StartAsync().ConfigureAwait(true);
        repository.PausedOperation = Guid.NewGuid();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<DurableReceipt> writing = peer.ReplicateAsync(repository.PausedOperation,
            [new("workflow:confirmation", "value", null, NodeValue.FromObject(42))], timeout.Token).AsTask();
        Task<Exception?> completion = Record.ExceptionAsync(() => writing);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable writingLifetime = new TestTaskLifetime(completion, timeout).ConfigureAwait(true);
        try
        {
            await repository.ReadCaptured.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            string key = repository.PausedOperation.ToString("N");
            DurableEntry previous = (await repository.ReadAsync("cluster:operations", key, timeout.Token).ConfigureAwait(true))!;
            DurableReceipt different = previous.Value!.Json.Deserialize<DurableReceipt>()! with { Fingerprint = "different-result", Outcome = EOutcome.Succeeded };
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(),
                [new("cluster:operations", key, previous.Version, NodeValue.FromObject(different))], timeout.Token).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            repository.ContinueWrite.TrySetResult();
        }
        Assert.IsType<InvalidDataException>(await completion.ConfigureAwait(true));
        Assert.Equal(1, repository.Commits);
    }

    /// <summary>実複製経路を使う一台の試験クラスタを作ります。</summary>
    /// <returns>通常参加条件を満たせる試験Peerです。</returns>
    private static PeerCluster Create(IDurableRepository repository) => new(new ClusterOptions(Guid.NewGuid().ToString("N"), "local", "local",
        "127.0.0.1", "127.255.255.255", LargeSynchronizationTests.Port(), 1, true, Path.GetTempPath()), repository)
    { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>内容のコミット後、結果保存用の読取りだけを一度止めます。</summary>
    private sealed class ConfirmationRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();
        private int captured;

        internal Guid PausedOperation
        {
            get; set;
        }

        internal int Commits
        {
            get; private set;
        }

        internal TaskCompletionSource ReadCaptured
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ContinueWrite
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>古い期待版を保持したまま、別の確認処理を先へ進めます。</summary>
        /// <returns>競合前に読み取った値です。</returns>
        public override async ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
        {
            DurableEntry? value = await base.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            string operationScope = "cluster:operations";
            string operationKey = PausedOperation.ToString("N");
            if (string.Equals(scope, operationScope, StringComparison.Ordinal) && string.Equals(key, operationKey, StringComparison.Ordinal)
                && value?.Value?.Json.Deserialize<DurableReceipt>()?.Outcome == EOutcome.Unknown
                && (await base.ReadReceiptAsync(PausedOperation, cancellationToken).ConfigureAwait(false))?.Outcome == EOutcome.Succeeded
                && Interlocked.CompareExchange(ref captured, 1, 0) == 0)
            {
                ReadCaptured.TrySetResult();
                await ContinueWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return value;
        }

        /// <summary>元の内容を適用する回数を数えます。</summary>
        /// <returns>実際のコミット結果です。</returns>
        public override ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            if (operationId == PausedOperation)
            {
                Commits++;
            }
            return base.CommitAsync(operationId, cancellationToken);
        }

        /// <summary>製品と同じ保存規則に、排他的な試験文書を渡します。</summary>
        /// <returns>一取引の結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
