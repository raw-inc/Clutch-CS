using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Snapshots;

/// <summary>状態互換性だけが解決済みの設定を参照し、構成保存を変えないことを検証します。</summary>
public sealed class SettingsStateCompatibilityTests
{
    /// <summary>未加工の dedupe設定が同じでも、渡された実効キーが違えば元状態契約と不一致です。</summary>
    /// <returns>実リポジトリの状態スナップショットと共通compilerによる照合です。</returns>
    [Fact]
    public async Task UpstreamSettingChangeChangesStateContractWithoutChangingRawDedupeNode()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        PeerCluster cluster = new(new("settings-state", "a", "host", "127.0.0.1", "127.255.255.255", LargeSynchronizationTests.Port(), 0, true, files.Root), repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = cluster.ConfigureAwait(true);
        WorkflowCompiler compiler = new(new NodeRegistry([new ConstantValueNode(), .. StateNodeRegistration.Create(new NodeStateStore(repository, "a"))]));
        WorkflowDefinition definition = new("demo", "demo",
            [new("key", "value.constant", JsonSerializer.SerializeToElement(new { text = "first", type = "string" })),
             new("marker", "state.dedupe", JsonSerializer.SerializeToElement(new { scope = "workflow" }))],
            [new("key", "value", "marker", SettingPorts.Name("key"))]);
        string originalNode = ContractJson.Hash(definition.Nodes[1]);
        NodeStateSnapshotParticipant participant = new("workflow:demo", "workflow:demo", repository, cluster, () => definition,
            raw => compiler.Compile(raw).Workflow?.OrderedNodes);
        NodeValue snapshot = await participant.CaptureAsync();
        Assert.True(participant.IsCompatible(snapshot, definition, true));
        definition = definition with { Nodes = [definition.Nodes[0] with { Settings = JsonSerializer.SerializeToElement(new { text = "second", type = "string" }) }, definition.Nodes[1]] };
        Assert.Equal(originalNode, ContractJson.Hash(definition.Nodes[1]));
        Assert.False(participant.IsCompatible(snapshot, definition, true));
        Assert.False(participant.IsCompatible(snapshot, null, false));
        Assert.Empty(await repository.ListAsync("workflow:demo"));
        NodeStateSnapshotParticipant unresolved = new("workflow:demo", "workflow:demo", repository, cluster, () => definition);
        Assert.False(unresolved.IsCompatible(snapshot, definition, true));
    }
}
