using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実行区間に対応する選択範囲を表示します。</summary>
public sealed class GraphRegionPresentationTests
{
    /// <summary>入れ子・開始・終了・最上位の選択を、配置変更とは独立して保持します。</summary>
    [Fact]
    public void SelectedScopeUsesNearestRegionAndTopLevelUsesAll()
    {
        WorkflowDefinition definition = new("workflow", "Workflow", [Node("top", "value.constant"), Node("loop", IterationNodeTypes.ForEach),
            Node("limit", ConcurrencyNodeTypes.Start), Node("body", "control.delay"), Node("limit-end", ConcurrencyNodeTypes.End), Node("loop-end", IterationNodeTypes.End), Node("after", "control.delay")],
            [new("loop", "item", "limit", "value"), new("loop", "loop", "loop-end", "loop"), new("limit", "value", "body", "value"),
             new("limit", "scope", "limit-end", "scope"), new("body", "value", "limit-end", "value"), new("limit-end", "value", "loop-end", "value"), new("loop-end", "value", "after", "value")]);
        GraphRegionPresentation presentation = new();
        presentation.Update(definition);
        string inner = presentation.SelectedScope("limit"), outer = presentation.SelectedScope("loop");
        Assert.NotEqual(inner, outer);
        Assert.Equal(inner, presentation.SelectedScope("body"));
        Assert.Equal(inner, presentation.SelectedScope("limit-end"));
        Assert.Equal(outer, presentation.SelectedScope("loop-end"));
        Assert.Equal("0", presentation.SelectedScope("after"));
        Assert.Equal("0", presentation.SelectedScope("top"));
        Assert.Equal(new[] { inner, outer }.Order(), presentation.Membership("body")!.Split(' ').Order());
        presentation.Update(definition with { Nodes = definition.Nodes.Select(node => node with { LogicalName = "Renamed" }).ToArray() });
        Assert.Equal(inner, presentation.SelectedScope("body"));
        presentation.Update(definition with { Wires = [] });
        Assert.Equal("0", presentation.SelectedScope("body"));
    }

    /// <summary>区間とカードの表示に必要な最小ノードです。</summary>
    /// <returns>入力値を保持しない定義です。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));
}
