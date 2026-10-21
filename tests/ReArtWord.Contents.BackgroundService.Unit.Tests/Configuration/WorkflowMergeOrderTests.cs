using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>統合不要時の配列順と、実際の三者統合を区別します。</summary>
public sealed class WorkflowMergeOrderTests
{
    /// <summary>三者同じ・片側のみ変更・両側同じ変更では採用定義の順序を保持します。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SelectedDefinitionRetainsOrder(int mode)
    {
        WorkflowDefinition basis = Definition();
        WorkflowDefinition current = basis;
        WorkflowDefinition edited = basis;
        WorkflowDefinition expected = basis;
        if (mode == 1 || mode == 3)
        {
            current = basis with { Name = "changed" };
            expected = current;
        }
        if (mode == 2 || mode == 3)
        {
            edited = basis with { Name = "changed" };
            expected = edited;
        }
        WorkflowMergeResult result = Merge(basis, current, edited);
        Assert.Empty(result.Conflicts);
        Assert.Equal(ContractJson.Hash(expected), ContractJson.Hash(result.Candidate));
        Assert.Equal(["number", "sum", "bytes"], result.Candidate!.Nodes.Select(node => node.Id));
    }

    /// <summary>別々の追加・削除を統合し、同一項目競合と識別子衝突を従来どおり残します。</summary>
    [Fact]
    public void RealMergeAndIdentityValidationRemainActive()
    {
        WorkflowDefinition basis = Definition();
        WorkflowDefinition current = basis with { Nodes = basis.Nodes.Take(2).ToArray() };
        WorkflowDefinition edited = basis with { Nodes = [..basis.Nodes, new("extra", "control.pass", JsonSerializer.Deserialize<JsonElement>("{}"))] };
        WorkflowMergeResult merged = Merge(basis, current, edited);
        Assert.Empty(merged.Conflicts);
        Assert.Equal(["extra", "number", "sum"], merged.Candidate!.Nodes.Select(node => node.Id));
        Assert.NotEmpty(Merge(basis, basis with { Name = "current" }, basis with { Name = "edited" }).Conflicts);
        WorkflowDefinition duplicate = basis with { Nodes = [..basis.Nodes, basis.Nodes[0]] };
        Assert.Null(Merge(duplicate, duplicate, duplicate).Candidate);
    }

    /// <summary>並べ替え以外は同じ入力を用意します。</summary>
    /// <returns>三つのノードを持つ定義です。</returns>
    private static WorkflowDefinition Definition()
    {
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("{}");
        return new("flow", "flow", [new("number", "value.constant", settings), new("sum", "math.add", settings), new("bytes", "data.binary.serialize", settings)], []);
    }

    /// <summary>実際の共通統合器を呼びます。</summary>
    /// <returns>候補と競合です。</returns>
    private static WorkflowMergeResult Merge(WorkflowDefinition basis, WorkflowDefinition current, WorkflowDefinition edited)
        => WorkflowEditorMerge.Merge(basis, current, new(Guid.NewGuid(), "base", edited, new Dictionary<string, EEditResolution>()));
}
