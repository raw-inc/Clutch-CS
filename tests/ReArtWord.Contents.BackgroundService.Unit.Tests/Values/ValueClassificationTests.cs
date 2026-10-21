using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Nodes.Comparison;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Logic;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Values;

/// <summary>表示分類の追加を静的評価能力の追加と混同しません。</summary>
public sealed class ValueClassificationTests
{
    /// <summary>値の演算分類を確認し、IValueNodeの対応範囲を変えません。</summary>
    [Fact]
    public void RegistryMarksValueOperationsWithoutPromotingEvaluation()
    {
        INode[] nodes = [.. DataNodeRegistration.Create(), .. ArithmeticNodeRegistration.Create(), .. ComparisonNodeRegistration.Create(), .. LogicNodeRegistration.Create(), new PassNode(), .. ValuesNodeRegistration.Create()];
        NodeRegistry registry = new(nodes);
        Assert.Equal(nodes.Length, registry.Descriptors.Count);
        Assert.All(registry.Descriptors, descriptor => Assert.True(descriptor.IsValueOperation));
        Assert.All(registry.Descriptors, descriptor => Assert.Equal(ENodeKind.Action, descriptor.Kind));
        Dictionary<string, NodeDescriptor> descriptors = registry.Descriptors.ToDictionary(descriptor => descriptor.Type, StringComparer.Ordinal);
        Assert.All(nodes, node => Assert.Equal(node is IValueNode, descriptors[node.Descriptor.Type].CanEvaluateStatically));
        Assert.All(DataNodeRegistration.Create(), node => Assert.Equal("Data", descriptors[node.Descriptor.Type].Category));
        Assert.All(ArithmeticNodeRegistration.Create().Concat(ComparisonNodeRegistration.Create()).Concat(LogicNodeRegistration.Create()),
            node => Assert.Equal("Math", descriptors[node.Descriptor.Type].Category));
        Assert.False(registry.Find("data.convert") is IValueNode);
        Assert.False(registry.Find("control.pass") is IValueNode);
        Assert.IsAssignableFrom<IValueNode>(registry.Find("text.substring"));
        Assert.False(registry.Find("text.regex") is IValueNode);
        Assert.False(registry.Find("text.match") is IValueNode);
    }
}
