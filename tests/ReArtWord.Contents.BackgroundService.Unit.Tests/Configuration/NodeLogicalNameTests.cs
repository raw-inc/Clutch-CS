using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>任意の論理名を共有でき、既存の識別・待受・編集統合には表示情報として扱います。</summary>
public sealed class NodeLogicalNameTests
{
    /// <summary>未指定の旧定義のJSONを変えず、同名の配置を独立したIDで保持します。</summary>
    [Fact]
    public void OptionalDuplicateNamesRoundTripWithoutChangingIds()
    {
        NodeDefinition first = new("one", "control.preview", JsonSerializer.SerializeToElement(new { }));
        JsonElement legacy = JsonSerializer.SerializeToElement(first, ContractJson.Options);
        Assert.False(legacy.TryGetProperty("logicalName", out _));
        Assert.Null(legacy.Deserialize<NodeDefinition>(ContractJson.Options)!.LogicalName);
        const string name = "受信値 <zone>";
        WorkflowDefinition document = new("wf", "Workflow", [first with { LogicalName = name }, first with { Id = "two", LogicalName = name }], []);
        WorkflowDefinition restored = ContractJson.Copy(document);
        Assert.Equal(["one", "two"], restored.Nodes.Select(node => node.Id));
        Assert.All(restored.Nodes, node => Assert.Equal(name, node.LogicalName));
        WorkflowDefinition yaml = WorkflowYamlReader.Read("""
            id: named
            nodes:
              - id: node
                type: control.preview
                logicalName: 受信値
            """);
        Assert.Equal("受信値", Assert.Single(yaml.Nodes).LogicalName);
    }

    /// <summary>名称は位置と同じ表示変更で、他の編集者の入力変更と別項目として統合します。</summary>
    [Fact]
    public void RenamingIsAppearanceOnlyAndMergesWithExecutionEdits()
    {
        NodeDefinition node = new("node", "udp.receive", JsonSerializer.SerializeToElement(new { localPort = 9000 }));
        WorkflowDefinition original = new("wf", "Workflow", [node], []);
        WorkflowDefinition renamed = original with { Nodes = [node with { LogicalName = "受信" }] };
        Assert.Equal([EWorkflowChangeKind.Appearance], WorkflowChangeInspection.Kinds(original, renamed));
        Assert.Empty(WorkflowChangeInspection.ChangedNodes(original, renamed));
        WorkflowDefinition current = original with { Nodes = [node with { Settings = JsonSerializer.SerializeToElement(new { localPort = 9001 }) }] };
        WorkflowMergeResult result = WorkflowEditorMerge.Merge(original, current, new(Guid.NewGuid(), "base", renamed, new Dictionary<string, EEditResolution>()));
        Assert.Empty(result.Conflicts);
        NodeDefinition merged = Assert.Single(result.Candidate!.Nodes);
        Assert.Equal("受信", merged.LogicalName);
        Assert.Equal(9001, merged.Settings.GetProperty("localPort").GetInt32());
        EWorkflowChangeKind[] changes = [EWorkflowChangeKind.Values, EWorkflowChangeKind.Appearance];
        Assert.Equal(changes.Order(), WorkflowChangeInspection.Kinds(original, result.Candidate));
        Assert.Equal(["node"], WorkflowChangeInspection.ChangedNodes(original, result.Candidate));
    }
}
