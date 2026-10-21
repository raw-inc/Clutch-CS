using System.Text.Json;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
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
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>再起動で分岐した内部担当を、現在の設定・参加実体から再確定します。</summary>
public sealed class SourcePcAssignmentConflictTests
{
    /// <summary>旧実体同士の競合でも、Coordinatorが全候補を包含する新しい担当を保存します。</summary>
    /// <returns>競合の終了、現在実体の選択、再読での不要な再保存防止です。</returns>
    [Fact]
    public async Task CurrentCoordinatorRecomputesConflictedDerivedAssignmentsAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "local.db"));
        SqliteRepository branch = new(Path.Combine(files.Root, "branch.db"));
        PeerCluster peer = new(new ClusterOptions(Guid.NewGuid().ToString("N"), "local", "host-local", "127.0.0.1", "127.255.255.255",
            LargeSynchronizationTests.Port(), 100, true, files.Root), repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        peer.SetConflictReadinessCheck(_ => ValueTask.FromResult(true));
        await peer.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        NodeRegistry registry = new([new PcSelectionNode(), new ScheduleNode()]);
        WorkflowEngine engine = new(registry, peer, peer);
        SourceSupervisor sources = new(registry, peer, engine, new(peer));
        await using ConfiguredAsyncDisposable sourceLifetime = sources.ConfigureAwait(true);
        WorkflowDefinition definition = new("conflicted-assignment", "Assignment", [
            new("pc", PcSelectionPorts.SelectNode, NodeSettings.Schema("""{"mode":"selected","hostIds":["host-local"]}""")),
            new("timer", "scheduling.timer", NodeSettings.Schema("""{"mode":"once","at":"2099-01-01T00:00:00Z"}""")),
        ], [new("pc", "value", "timer", PcSelectionPorts.Execution)], Enabled: true);
        CompilationResult compiled = new WorkflowCompiler(registry).Compile(definition);
        Assert.Empty(compiled.Issues);
        CompiledWorkflow plan = Assert.IsType<CompiledWorkflow>(compiled.Workflow);
        await sources.ApplyAsync(plan, timeout.Token);
        PeerInfo[] members = peer.GetPeers().ToArray();
        TargetResolution selected = new PcSelectionResolver(members, new Dictionary<string, ResourceSample>(StringComparer.Ordinal), DateTimeOffset.UtcNow, peer.LocalInstanceId)
            .Resolve(plan.PcSelections!["timer"], EPcExecutionMode.One);
        string scope = SourcePcAssignments.ScopePrefix + definition.Id;
        foreach (SqliteRepository target in new[] { repository, branch })
        {
            SourcePcAssignment old = new(Guid.NewGuid(), definition.Id, "timer", plan.PcSelectionRevisions!["timer"],
                [members[0] with { InstanceId = Guid.NewGuid() }], selected.Selection!, members);
            Assert.Equal(EOutcome.Succeeded, (await target.ApplyBatchAsync(old.OperationId, [new(scope, "timer", null, NodeValue.FromObject(old))], timeout.Token)).Outcome);
        }
        Assert.False(await repository.ImportAsync(await branch.ExportAsync(timeout.Token), timeout.Token));
        SourcePcAssignments assignments = new(peer, repository, sources, registry, static () => new Dictionary<string, ResourceSample>(StringComparer.Ordinal));
        await assignments.RefreshAsync(timeout.Token);
        Assert.Empty(await repository.GetConflictsAsync(scope, timeout.Token));
        DurableEntry current = Assert.IsType<DurableEntry>(await repository.ReadAsync(scope, "timer", timeout.Token));
        SourcePcAssignment actual = current.Value!.Json.Deserialize<SourcePcAssignment>(ContractJson.Options)!;
        Assert.Equal(peer.LocalInstanceId, Assert.Single(actual.Targets).InstanceId);
        Assert.Equal(2, current.MergedVersions!.Count);
        await assignments.RefreshAsync(timeout.Token);
        Assert.Equal(current.Version, (await repository.ReadAsync(scope, "timer", timeout.Token))!.Version);
        Assert.Empty(await repository.GetConflictsAsync("workflows", timeout.Token));
    }
}
