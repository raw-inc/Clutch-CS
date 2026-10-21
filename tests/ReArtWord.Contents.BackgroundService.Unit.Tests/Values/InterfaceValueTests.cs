using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Values;

/// <summary>インターフェース値はOSへ照会せず元の識別子を保持します。</summary>
public sealed class InterfaceValueTests
{
    /// <summary>GUIDに限定せず将来利用する識別子も純粋値として返します。</summary>
    [Theory]
    [InlineData("eth0")]
    [InlineData("{FCD05786-90CD-4493-BA21-65C9DC9C45A6}")]
    [InlineData("future-device-not-installed")]
    public void IdentifierIsPreservedWithoutInventory(string identifier)
    {
        InterfaceValueNode node = new();
        NodeDefinition definition = new("interface", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { interfaceId = identifier }));
        NodeResult result = node.Evaluate(definition, new Dictionary<string, NodeValue>());
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(identifier, result.Outputs["value"].Json.GetString());
    }

    /// <summary>空の識別子は実行前に拒否します。</summary>
    [Fact]
    public void EmptyIdentifierIsNotExecuted()
    {
        InterfaceValueNode node = new();
        NodeResult result = node.Evaluate(new("interface", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { interfaceId = string.Empty })), new Dictionary<string, NodeValue>());
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(result.Outputs);
    }
}
