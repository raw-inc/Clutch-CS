using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>同じ担当条件で保存拒否を量産せず、回復後は自動で担当を確定します。</summary>
public sealed class SourcePcAssignmentRetryTests
{
    /// <summary>短周期の巡回では一回だけ保存を試し、期限後の成功で通常動作へ戻します。</summary>
    /// <returns>失敗履歴の増幅を止めつつ、担当確認を恒久停止しないことです。</returns>
    [Fact]
    public async Task RepeatedVersionConflictIsBackedOffAndRecoveryIsAutomaticAsync()
    {
        using MediaTestFiles files = new();
        RefusingRepository repository = new();
        PeerCluster peer = new(new ClusterOptions(Guid.NewGuid().ToString("N"), "local", "host-local", "127.0.0.1", "127.255.255.255",
            LargeSynchronizationTests.Port(), 1, true, files.Root), repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        await peer.StartAsync();
        NodeRegistry registry = new([new PcSelectionNode(), new ScheduleNode()]);
        WorkflowEngine engine = new(registry, peer, peer);
        SourceSupervisor sources = new(registry, peer, engine, new(peer));
        await using ConfiguredAsyncDisposable sourceLifetime = sources.ConfigureAwait(true);
        WorkflowDefinition definition = new("retry", "Retry", [
            new("pc", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-local"]}""")),
            new("timer", "scheduling.timer", NodeSettings.Schema("""{"mode":"once","at":"2099-01-01T00:00:00Z"}""")),
        ], [new("pc", "value", "timer", PcSelectionPorts.Execution)], Enabled: true);
        CompilationResult compiled = new WorkflowCompiler(registry).Compile(definition);
        Assert.Empty(compiled.Issues);
        await sources.ApplyAsync(Assert.IsType<CompiledWorkflow>(compiled.Workflow));
        MutableClock clock = new();
        SourcePcAssignments assignments = new(peer, repository, sources, registry, static () => new Dictionary<string, ResourceSample>(StringComparer.Ordinal), clock);
        for (int pass = 0; pass < 10; pass++)
        {
            await assignments.RefreshAsync();
        }
        Assert.Equal(1, repository.Preparations);
        Assert.Equal(1, repository.AssignmentReads);
        Assert.Equal(1, repository.ConflictReads);
        Assert.Null(assignments.Read(definition.Id, "timer"));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:change", "value", null, NodeValue.FromObject(1))]);
        await assignments.RefreshAsync();
        Assert.Equal(2, repository.AssignmentReads);
        Assert.Equal(1, repository.Preparations);
        await assignments.RefreshAsync();
        Assert.Equal(2, repository.AssignmentReads);
        repository.Reject = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        await assignments.RefreshAsync();
        Assert.Equal(2, repository.Preparations);
        Assert.NotNull(assignments.Read(definition.Id, "timer"));
        await assignments.RefreshAsync();
        Assert.Equal(2, repository.Preparations);
    }

    /// <summary>試験の担当再確認時刻だけを進め、実UDPの期限は変更しません。</summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        /// <summary>試験で指定した現在時刻です。</summary>
        /// <returns>UTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => now;

        /// <summary>担当確認の期限を進めます。</summary>
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    /// <summary>元の保存規則を保ち、準備拒否の回数だけを観測します。</summary>
    private sealed class RefusingRepository : DurableRepository, ISharedChangeStamp
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();

        /// <summary>小さい隔離保存先の変更位置を、待機解除の境界へ接続します。</summary>
        /// <returns>今回の保存状態の識別です。</returns>
        async ValueTask<string?> ISharedChangeStamp.ReadSharedChangeStampAsync(CancellationToken cancellationToken)
            => await GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);

        internal bool Reject
        {
            get; set;
        } = true;

        internal int Preparations
        {
            get; private set;
        }

        internal int AssignmentReads
        {
            get; private set;
        }

        internal int ConflictReads
        {
            get; private set;
        }

        /// <summary>担当本文を実際に復元した回数を観測します。</summary>
        /// <returns>元のリポジトリと同じ現在値です。</returns>
        public override ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
        {
            if (scope.StartsWith(SourcePcAssignments.ScopePrefix, StringComparison.Ordinal))
            {
                AssignmentReads++;
            }
            return base.ReadAsync(scope, key, cancellationToken);
        }

        /// <summary>待機中に同じ競合集合を繰り返し復元していないかを観測します。</summary>
        /// <returns>元の競合候補です。</returns>
        public override ValueTask<IReadOnlyList<DurableEntry>> GetConflictsAsync(string scope, CancellationToken cancellationToken = default)
        {
            if (scope.StartsWith(SourcePcAssignments.ScopePrefix, StringComparison.Ordinal))
            {
                ConflictReads++;
            }
            return base.GetConflictsAsync(scope, cancellationToken);
        }

        /// <summary>準備を確定拒否し、後から同じ通常経路で受理できるようにします。</summary>
        /// <returns>準備結果です。</returns>
        public override ValueTask<DurableReceipt> PrepareAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
        {
            Preparations++;
            if (Reject)
            {
                return ValueTask.FromResult(new DurableReceipt(operationId, RepositoryCausality.Fingerprint(mutations), EOutcome.Failed, [], "Persistence.VersionConflict"));
            }
            return base.PrepareAsync(operationId, mutations, cancellationToken);
        }

        /// <summary>実際の共通保存規則を排他的に実行します。</summary>
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
