using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>同じ所有定義の照合を再利用しても、編集と保存版識別を混同しません。</summary>
public sealed class WorkflowDraftComparisonTests
{
    /// <summary>表示順が違うACKを同じ内容と認識し、保存版の順序は維持します。</summary>
    [Fact]
    public void ReorderedAcknowledgementKeepsExactSaveRevision()
    {
        WorkflowDefinition first = Definition();
        WorkflowDefinition reordered = first with { Nodes = first.Nodes.Reverse().ToArray(), Wires = first.Wires.Reverse().ToArray() };
        Assert.True(WorkflowDraftOrder.Same(first, reordered));
        Assert.Equal(ContractJson.Hash(first), WorkflowDraftOrder.Revision(first));
        Assert.Equal(ContractJson.Hash(reordered), WorkflowDraftOrder.Revision(reordered));
        Assert.NotEqual(WorkflowDraftOrder.Revision(first), WorkflowDraftOrder.Revision(reordered));
    }

    /// <summary>次の編集、配線条件、JSON数値表現は旧版の比較結果を流用しません。</summary>
    [Fact]
    public void NewEditsAndWireConditionsRemainDistinct()
    {
        WorkflowDefinition first = Definition();
        WorkflowDefinition copy = ContractJson.Copy(first);
        Assert.True(WorkflowDraftOrder.Same(first, copy));
        WorkflowDefinition edited = copy with { Nodes = [copy.Nodes[0] with { Settings = JsonSerializer.SerializeToElement(new { number = 2 }) }, copy.Nodes[1]] };
        Assert.False(WorkflowDraftOrder.Same(first, edited));
        Assert.False(WorkflowDraftOrder.Same(first, copy with { Wires = [copy.Wires[0] with { When = "failure" }, copy.Wires[1]] }));
        Assert.False(WorkflowDraftOrder.Same(first, copy with { Nodes = [copy.Nodes[0] with { Settings = JsonSerializer.Deserialize<JsonElement>("{\"number\":1.0}") }, copy.Nodes[1]] }));
        Assert.True(WorkflowDraftOrder.Same(first, copy));
    }

    /// <summary>巨大な本文を含む同じ照合では、全体の直列化・配列・ハッシュ領域を繰り返し確保しません。</summary>
    [Fact]
    public void RepeatedChecksDoNotAllocateWholeDocuments()
    {
        WorkflowDefinition first = new("large", "large", [new("n", "value.constant", JsonSerializer.SerializeToElement(new { text = new string('x', 1048576) }))], []);
        WorkflowDefinition second = ContractJson.Copy(first);
        Assert.True(WorkflowDraftOrder.Same(first, second));
        string revision = WorkflowDraftOrder.Revision(first);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20; index++)
        {
            Assert.True(WorkflowDraftOrder.Same(first, second));
            Assert.Equal(revision, WorkflowDraftOrder.Revision(first));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 65536);
    }

    /// <summary>配線順を変えられる複数ノードの草稿です。</summary>
    /// <returns>数値表現を含む比較用定義です。</returns>
    private static WorkflowDefinition Definition() => new("flow", "flow",
        [new("b", "value.constant", JsonSerializer.SerializeToElement(new { number = 1 })), new("a", "value.constant", JsonSerializer.SerializeToElement(new { text = "second" }))],
        [new("b", "value", "a", "left"), new("a", "value", "b", "right")]);
}
