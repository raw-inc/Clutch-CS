using System.Text.Json;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>一時的に利用できないPCを共有担当として固定せず、復帰後の実体を採用します。</summary>
public sealed class SourcePcAssignmentAvailabilityTests
{
    /// <summary>実参加・離脱と旧形式の利用不可記録を経て、利用可能な担当だけを保存します。</summary>
    /// <returns>未配置時の保存抑制、復帰後の担当と安定した再読です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnavailableSelectionIsNotPersistedAndLegacyFailureIsReplaced(bool legacyFailure, bool includeLocal)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-source-availability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string clusterId = Guid.NewGuid().ToString("N");
            int port = LargeSynchronizationTests.Port();
            SqliteRepository repository = new(Path.Combine(root, "a.db"));
            SqliteRepository remoteRepository = new(Path.Combine(root, "b.db"));
            PeerCluster first = Peer(repository, root, clusterId, "a", port, true);
            await using ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
            await first.StartAsync().ConfigureAwait(true);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            PeerCluster remote = Peer(remoteRepository, root, clusterId, "b", port, false);
            await using (remote.ConfigureAwait(true))
            {
                await remote.StartAsync(timeout.Token).ConfigureAwait(true);
                await WaitAsync(first, EPeerStatus.Ready, timeout.Token).ConfigureAwait(true);
            }
            await WaitAsync(first, EPeerStatus.Unavailable, timeout.Token).ConfigureAwait(true);
            NodeRegistry registry = new([new PcSelectionNode(), new ScheduleNode()]);
            WorkflowEngine engine = new(registry, first, first);
            SourceSupervisor sources = new(registry, first, engine, new(first));
            await using ConfiguredAsyncDisposable sourceLifetime = sources.ConfigureAwait(true);
            WorkflowDefinition definition = new("availability", "Source availability", [
                new("pc", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-b"]}""")),
                new("timer", "scheduling.timer", NodeSettings.Schema("""{"mode":"once","at":"2099-01-01T00:00:00Z"}""")),
            ], [new("pc", "value", "timer", PcSelectionPorts.Execution)], Enabled: true);
            if (includeLocal)
            {
                definition = definition with { Nodes = [definition.Nodes[0] with { Settings = NodeSettings.Schema("""{"mode":"selected","hostIds":["host-a","host-b"]}""") },
                    definition.Nodes[1] with { PcExecution = EPcExecutionMode.All }] };
            }
            CompilationResult compiled = new WorkflowCompiler(registry).Compile(definition);
            Assert.Empty(compiled.Issues);
            CompiledWorkflow plan = Assert.IsType<CompiledWorkflow>(compiled.Workflow);
            await sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
            SourcePcAssignments assignments = new(first, repository, sources, registry, static () => new Dictionary<string, ResourceSample>(StringComparer.Ordinal));
            string scope = SourcePcAssignments.ScopePrefix + definition.Id;
            await assignments.RefreshAsync(timeout.Token).ConfigureAwait(true);
            if (includeLocal)
            {
                DurableEntry partial = Assert.IsType<DurableEntry>(await repository.ReadAsync(scope, "timer", timeout.Token).ConfigureAwait(true));
                SourcePcAssignment assignedPartial = partial.Value!.Json.Deserialize<SourcePcAssignment>(ContractJson.Options)!;
                Assert.Equal(first.LocalInstanceId, Assert.Single(assignedPartial.Targets).InstanceId);
            }
            else
            {
                if (!legacyFailure)
                {
                    Assert.Null(await repository.ReadAsync(scope, "timer", timeout.Token).ConfigureAwait(true));
                }
            }
            PeerCluster returned = Peer(remoteRepository, root, clusterId, "b", port, false);
            await using ConfiguredAsyncDisposable returnedLifetime = returned.ConfigureAwait(true);
            await returned.StartAsync(timeout.Token).ConfigureAwait(true);
            await WaitAsync(first, EPeerStatus.Ready, timeout.Token).ConfigureAwait(true);
            if (legacyFailure)
            {
                PeerInfo[] members = first.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
                TargetResolution choice = new PcSelectionResolver(members, new Dictionary<string, ResourceSample>(StringComparer.Ordinal), DateTimeOffset.UtcNow, first.LocalInstanceId)
                    .Resolve(plan.PcSelections!["timer"], EPcExecutionMode.One);
                SourcePcAssignment failed = new(Guid.NewGuid(), definition.Id, "timer", plan.PcSelectionRevisions!["timer"],
                    choice.Peers.Select(peer => peer with { Status = EPeerStatus.Unavailable, ReadySince = null }).ToArray(),
                    choice.Selection! with { CandidateProblems = new Dictionary<string, string>(StringComparer.Ordinal) { ["host-b"] = "selection.pcUnavailable" } }, members);
                DurableEntry? current = await repository.ReadAsync(scope, "timer", timeout.Token).ConfigureAwait(true);
                DurableReceipt saved = await first.ReplicateFixedAsync(failed.OperationId, [new(scope, "timer", current?.Version, NodeValue.FromObject(failed))], members, timeout.Token).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, saved.Outcome);
            }
            await assignments.RefreshAsync(timeout.Token).ConfigureAwait(true);
            DurableEntry ready = Assert.IsType<DurableEntry>(await repository.ReadAsync(scope, "timer", timeout.Token).ConfigureAwait(true));
            SourcePcAssignment assigned = ready.Value!.Json.Deserialize<SourcePcAssignment>(ContractJson.Options)!;
            string remotePeer = "b";
            Assert.Equal(returned.LocalInstanceId, Assert.Single(assigned.Targets, peer => string.Equals(peer.PeerId, remotePeer, StringComparison.Ordinal)).InstanceId);
            Assert.Equal(includeLocal ? 2
                                      : 1, assigned.Targets.Count);
            Assert.All(assigned.Targets, peer => Assert.True(PeerStatusRules.CanParticipate(peer.Status)));
            Assert.Empty(assigned.Decision.CandidateProblems ?? new Dictionary<string, string>(StringComparer.Ordinal));
            await assignments.RefreshAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(ready.Version, (await repository.ReadAsync(scope, "timer", timeout.Token).ConfigureAwait(true))!.Version);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>同じPC識別子と異なる起動実体を実UDPへ接続します。</summary>
    /// <returns>短い生存期限を使う試験Peerです。</returns>
    private static PeerCluster Peer(SqliteRepository repository, string root, string cluster, string peer, int port, bool initialize) => new(
        new ClusterOptions(cluster, peer, "host-" + peer, "127.0.0.1", "127.255.255.255", port, 100, initialize, root)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromMilliseconds(500), RequestTimeout = TimeSpan.FromSeconds(1),
        }, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>送信された参加状態の反映だけを待ちます。</summary>
    /// <returns>観測条件の成立です。</returns>
    private static async Task WaitAsync(PeerCluster first, EPeerStatus status, CancellationToken cancellationToken)
    {
        string remote = "b";
        while (!first.GetPeers().Any(peer => string.Equals(peer.PeerId, remote, StringComparison.Ordinal) && peer.Status == status))
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }
}
