using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Values;

/// <summary>検査を共有しても同じ種類のノードや別の編集版の型を混同しません。</summary>
public sealed class WorkflowConnectionInspectionTests
{
    /// <summary>異なる構造のプロパティ口と出力型を、検査順に依存せず保持します。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctObjectsKeepTheirOwnPortsAndTypes(bool reverse)
    {
        NodeDefinition first = new("first", "value.constant", JsonSerializer.SerializeToElement(new { type = "auto", text = "{\"a\":1}" }));
        NodeDefinition second = new("second", "value.constant", JsonSerializer.SerializeToElement(new { type = "auto", text = "{\"b\":true}" }));
        NodeDefinition sink = new("sink", "test.sink", JsonSerializer.SerializeToElement(new { }));
        WireDefinition number = new("first", "value/a", "sink", "number"), boolean = new("second", "value/b", "sink", "boolean");
        WorkflowDefinition document = new("inspection", "Inspection", [first, second, sink], [number, boolean]);
        NodeDescriptor[] catalog = [Descriptor("value.constant"), Descriptor("test.sink") with
        {
            Inputs = [new("number", EPortValueKind.Json) { DataType = new("integer") }, new("boolean", EPortValueKind.Json) { DataType = new("boolean") }],
        }];
        WorkflowConnectionInspection inspection = new(document, catalog);
        WireDefinition[] wires = reverse ? [boolean, number]
                                         : [number, boolean];
        foreach (WireDefinition wire in wires)
        {
            Assert.True(WorkflowConnectionTypes.CanConnect(inspection, wire));
        }
        Assert.DoesNotContain(inspection.GetPorts("first")!.Outputs, port => port.Name is "value/b");
        Assert.DoesNotContain(inspection.GetPorts("second")!.Outputs, port => port.Name is "value/a");
        Assert.False(WorkflowConnectionTypes.CanConnect(inspection, number with { ToPort = "boolean" }));
        WorkflowDefinition edited = document with { Nodes = [first with { Settings = JsonSerializer.SerializeToElement(new { type = "auto", text = "{\"a\":\"wrong\"}" }) }, second, sink] };
        Assert.False(WorkflowConnectionTypes.CanConnect(new WorkflowConnectionInspection(edited, catalog), number));
        Assert.True(WorkflowConnectionTypes.CanConnect(inspection, number));
    }

    /// <summary>型検査だけに必要な、外部処理を持たない契約です。</summary>
    /// <returns>JSON出力の宣言です。</returns>
    private static NodeDescriptor Descriptor(string type) => new(type, type, "Test", "", ENodeKind.Action, EExecutionLocation.Independent,
        [], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
