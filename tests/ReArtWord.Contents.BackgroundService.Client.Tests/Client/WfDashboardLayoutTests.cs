using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>カードの移動で参照先を変えず、保存後にも表示順を復元できることを検証します。</summary>
public sealed class WfDashboardLayoutTests
{
    /// <summary>前後・末尾への移動と、同位置や画面から消えたカードへの無操作を確認します。</summary>
    [Theory]
    [InlineData("c", "a", "c,a,b")]
    [InlineData("a", "c", "b,a,c")]
    [InlineData("a", null, "b,c,a")]
    [InlineData("b", "b", "a,b,c")]
    [InlineData("a", "b", "a,b,c")]
    [InlineData("c", null, "a,b,c")]
    [InlineData("missing", "a", "a,b,c")]
    [InlineData("a", "missing", "a,b,c")]
    public void ReorderingPreservesReferencesAndSurvivesSerialization(string id, string? before, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string[] ids = ["a", "b", "c"];
        NodeDefinition[] cards = ids.Select((key, index) => new NodeDefinition(key, WfDashboardReference.CardType,
            new WfDashboardReference("source", key, "value.constant").ToSettings(), Position: new(index * 10, 4))).ToArray();
        NodeDefinition[] moved = WfDashboardWorkspace.ReorderCards(cards, id, before);
        WorkflowDefinition restored = ContractJson.Copy(new WorkflowDefinition("dashboard", "Dashboard", moved, []) { IsDashboard = true });
        Assert.Equal(expected.Split(','), restored.Nodes.OrderBy(card => card.Position!.Y).ThenBy(card => card.Position!.X).Select(card => card.Id));
        foreach (NodeDefinition card in restored.Nodes)
        {
            Assert.Equal(new WfDashboardReference("source", card.Id, "value.constant"), WfDashboardReference.Read(card));
        }
        Assert.Equal(ids, cards.Select(card => card.Id));
        if (ids.SequenceEqual(expected.Split(',')))
        {
            Assert.Same(cards, moved);
        }
    }
}
