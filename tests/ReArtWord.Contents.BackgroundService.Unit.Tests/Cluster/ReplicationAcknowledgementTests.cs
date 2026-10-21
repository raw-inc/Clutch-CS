using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>コミット応答を失っても、元の全参加者の永続結果だけで確認を補う境界です。</summary>
public sealed class ReplicationAcknowledgementTests
{
    /// <summary>実UDPの準備・コミット応答を止め、照会で一致する保存状態だけを採用します。</summary>
    /// <returns>未確認の操作を再適用せず返した結果です。</returns>
    [Theory]
    [InlineData(0, EOutcome.Succeeded, false, false, false)]
    [InlineData(0, EOutcome.Succeeded, true, false, false)]
    [InlineData(1, EOutcome.Unknown, false, false, false)]
    [InlineData(1, EOutcome.Unknown, true, false, false)]
    [InlineData(2, EOutcome.Unknown, false, false, false)]
    [InlineData(2, EOutcome.Unknown, true, false, false)]
    [InlineData(0, EOutcome.Succeeded, true, true, false)]
    [InlineData(1, EOutcome.Unknown, true, true, false)]
    [InlineData(0, EOutcome.Succeeded, false, false, true)]
    [InlineData(2, EOutcome.Unknown, false, false, true)]
    [InlineData(0, EOutcome.Succeeded, true, true, true)]
    [InlineData(1, EOutcome.Unknown, true, true, true)]
    public async Task LostPhaseAcknowledgementUsesMatchingDurableStateAsync(int receiptMode, EOutcome expected, bool preparation, bool compressed, bool atomic)
    {
        using MediaTestFiles files = new();
        string clusterId = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        IDurableRepository local = new DelayedCommitRepository();
        if (atomic)
        {
            local = new SqliteRepository(Path.Combine(files.Root, "state.db"));
        }
        DelayedCommitRepository remote = new() { ReceiptMode = receiptMode, DelayPreparation = preparation };
        await using PeerCluster first = Create(local, clusterId, "first", port, true);
        await using PeerCluster second = Create(remote, clusterId, "second", port, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        Guid operation = Guid.NewGuid();
        remote.DelayedOperation = operation;
        PeerInfo[] members = first.GetPeers().OrderByDescending(peer => peer.InstanceId == first.LocalInstanceId).ToArray();
        NodeValue value = compressed ? NodeValue.FromObject(new string('x', 131072))
                                     : NodeValue.FromObject(42);
        DurableReceipt result;
        try
        {
            result = await first.ReplicateFixedAsync(operation, [new("configuration", "confirmed", null, value)], members, timeout.Token).ConfigureAwait(true);
            Assert.Equal(expected, result.Outcome);
            Assert.Equal(expected, (await first.GetOperationAsync(operation, timeout.Token).ConfigureAwait(true))?.Outcome);
            Assert.Equal(1, remote.DelayedPhases);
            Assert.True(0 < remote.Queries);
            if (!preparation || expected == EOutcome.Succeeded)
            {
                Assert.Equal(value.Json.GetRawText(), (await remote.ReadAsync("configuration", "confirmed", timeout.Token).ConfigureAwait(true))?.Value?.Json.GetRawText());
            }
            else
            {
                Assert.Null(await remote.ReadAsync("configuration", "confirmed", timeout.Token).ConfigureAwait(true));
                Assert.Null(await remote.ReadPreparationAsync(operation, timeout.Token).ConfigureAwait(true));
                Assert.Null(await local.ReadAsync("configuration", "confirmed", timeout.Token).ConfigureAwait(true));
                Assert.Null(await local.ReadPreparationAsync(operation, timeout.Token).ConfigureAwait(true));
            }
            if (expected == EOutcome.Unknown)
            {
                Assert.Equal("Replication.RequiredAcknowledgementMissing", result.Reason);
            }
        }
        finally
        {
            remote.Release.TrySetResult();
        }
    }

    /// <summary>一回のUDP応答期限だけを短くした通常Peerです。</summary>
    /// <returns>実発見・実同期を行うPeerです。</returns>
    private static PeerCluster Create(IDurableRepository repository, string cluster, string peer, int port, bool fresh) => new(
        new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath())
        { RequestTimeout = TimeSpan.FromMilliseconds(500), HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, repository)
    { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>内容のコミットと応答を分離し、照会の欠測・不一致も再現します。</summary>
    private sealed class DelayedCommitRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();

        internal Guid DelayedOperation
        {
            get; set;
        }

        internal int ReceiptMode
        {
            get; init;
        }

        internal int DelayedPhases
        {
            get; private set;
        }

        internal int Queries
        {
            get; private set;
        }

        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool DelayPreparation
        {
            get; init;
        }

        /// <summary>保存できた準備だけの応答を保留し、副作用を伴わない照会を可能にします。</summary>
        /// <returns>元の一回の準備結果です。</returns>
        public override async ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
        {
            DurableReceipt result = await base.PrepareAsync(operationId, mutations, cancellationToken).ConfigureAwait(false);
            if (operationId == DelayedOperation && DelayPreparation)
            {
                DelayedPhases++;
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>未準備・異なる内容を、同じ内容の準備済み結果として採用させません。</summary>
        /// <returns>元の準備指紋、欠測、または不一致です。</returns>
        public override async ValueTask<string?> ReadPreparationAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            string? fingerprint = await base.ReadPreparationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operationId == DelayedOperation)
            {
                Queries++;
                return ReceiptMode switch { 1 => null, 2 when fingerprint is not null => "mismatched-content", _ => fingerprint };
            }
            return fingerprint;
        }

        /// <summary>永続結果の作成後にだけ、元のコミット応答を止めます。</summary>
        /// <returns>試験終了時に解放する元結果です。</returns>
        public override async ValueTask<DurableReceipt> CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            DurableReceipt result = await base.CommitAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operationId == DelayedOperation && !DelayPreparation)
            {
                DelayedPhases++;
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>同じ元操作の照会だけに指定した障害を返します。</summary>
        /// <returns>一致結果、欠測、または不一致結果です。</returns>
        public override async ValueTask<DurableReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            DurableReceipt? receipt = await base.ReadReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operationId == DelayedOperation)
            {
                Queries++;
                if (ReceiptMode == 1)
                {
                    return null;
                }
                if (ReceiptMode == 2 && receipt is not null)
                {
                    return receipt with { Fingerprint = "mismatched-content" };
                }
            }
            return receipt;
        }

        /// <summary>製品と同じ取引規則を排他的な試験文書へ適用します。</summary>
        /// <returns>保存操作の結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
