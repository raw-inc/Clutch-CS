using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>到着を待つ口の構成制約と、変換を伴わない型推測を確認します。</summary>
public sealed class ValueMergeDefinitionTests
{
    /// <summary>同型の入力だけなら引継ぎ、異種を許す場合はanyとして表示します。</summary>
    [Theory]
    [InlineData("string", "one", "string", "two", "string")]
    [InlineData("integer", "1", "integer", "2", "integer")]
    [InlineData("integer", "1", "string", "two", "any")]
    [InlineData("auto", "null", "integer", "2", "any")]
    public void InfersCommonTypeWithoutConvertingArrivals(string firstType, string firstValue, string secondType, string secondValue, string expected)
    {
        NodeRegistry registry = new([new ConstantValueNode(), new ValueMergeNode()]);
        NodeDefinition merge = WorkflowFixture.Definition("merge", ValueMergePorts.NodeType);
        WorkflowDefinition definition = new("types", "Types", [
            new("a", "value.constant", JsonSerializer.SerializeToElement(new { type = firstType, text = firstValue })),
            new("b", "value.constant", JsonSerializer.SerializeToElement(new { type = secondType, text = secondValue })), merge],
            [new("a", "value", "merge", "input1"), new("b", "value", "merge", "input2")]);
        Assert.All(definition.Wires, wire => Assert.True(WorkflowConnectionTypes.CanConnect(definition, registry.Descriptors, wire)));
        Assert.Equal(expected, new WorkflowTypeInference(definition, registry.Descriptors).PortType("merge", "value", true)!.Type);
        Assert.Equal("boolean", new WorkflowTypeInference(definition, registry.Descriptors).PortType("merge", "start", true)!.Type);
        Assert.False(WorkflowConnectionTypes.CanConnect(definition, registry.Descriptors, new("a", "value", "merge", "trigger")));
        Assert.Null(new WorkflowSettingValues(definition).Output("merge"));
    }

    /// <summary>定数や全PCへの重複配送を保存前に拒否します。</summary>
    [Fact]
    public void RejectsStaticInputsAndMultipleMergeExecutors()
    {
        ValueMergeNode merge = new();
        NodeDefinition definition = WorkflowFixture.Definition("merge", ValueMergePorts.NodeType);
        Assert.Empty(merge.Validate(definition));
        Assert.Contains(merge.Validate(definition with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input1"] = NodeValue.Null } }), issue => issue.Code is "valueMerge.connections");
        Assert.Contains(merge.Validate(definition with { PcExecution = EPcExecutionMode.All }), issue => issue.Code is "valueMerge.connections");
        Assert.Contains(merge.Validate(definition with { Target = new(EExecutionScope.All, ["a", "b"]) }), issue => issue.Code is "valueMerge.connections");
        Assert.True(StartSignalPorts.IsOutput(merge.Descriptor, "start"));
        Assert.False(StartSignalPorts.IsOutput(merge.Descriptor, "value"));
    }
}
