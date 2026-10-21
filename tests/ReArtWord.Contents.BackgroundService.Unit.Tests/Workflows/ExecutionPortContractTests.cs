using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実行時の入力契約を保ち、描画専用の派生出力を毎回生成しません。</summary>
public sealed class ExecutionPortContractTests
{
    /// <summary>多数のプロパティを持つObjectでも、入力と元の出力契約は編集時と一致します。</summary>
    [Fact]
    public void ExecutionContractsDoNotAllocatePropertyOutputs()
    {
        NodeDescriptor descriptor = new ExpandObjectNode().Descriptor;
        JsonElement settings = JsonSerializer.SerializeToElement(new
        {
            objectMode = "manual",
            objectType = new { type = "object", properties = Enumerable.Range(0, 96).ToDictionary(index => "field" + index, _ => new { type = "string" }) },
        });
        NodeDefinition node = new("expand", descriptor.Type, settings);
        NodeDescriptor editor = ConfiguredNodePorts.Resolve(descriptor, node)!;
        NodeDescriptor execution = ConfiguredNodePorts.ResolveExecution(descriptor, node)!;
        Assert.Equal(96, editor.Outputs.Count(port => PropertyPorts.Parse(port.Name) is not null));
        Assert.Equal(editor.Inputs, execution.Inputs);
        Assert.Equal(editor.Outputs.Where(port => PropertyPorts.Parse(port.Name) is null), execution.Outputs);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 500; index++)
        {
            _ = ConfiguredNodePorts.ResolveExecution(descriptor, node);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 1048576);
    }
}
