using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>検索の先頭候補と新しい入力後の選択を検証します。</summary>
public sealed class NodeQuickSearchTests
{
    /// <summary>元の名称がValue typeでも、画面上のType definitionをValue mergeより上にしません。</summary>
    [Fact]
    public void VisibleNamesPrecedeHiddenOriginalNameAliases()
    {
        NodeDescriptor value = Create("value.constant", "Value", "Values", "Constant.");
        NodeDescriptor type = Create("value.type", "Value type", "Values", "Type.");
        NodeDescriptor merge = Create("control.valueMerge", "Value merge", "Control", "Forward.");
        IReadOnlyList<NodeDescriptor> result = NodeQuickSearch.Find([type, merge, value], "Val", null,
            item =>
            {
                if (ReferenceEquals(item, type))
                {
                    return "Type definition";
                }
                return item.DisplayName;
            }, item => item.Description);
        Assert.Equal([value.Type, merge.Type, type.Type], result.Select(item => item.Type));
    }

    /// <summary>Valでは名前がValueの候補を、内部型がvalueで始まる別名候補より先にします。</summary>
    [Fact]
    public void NamePrefixWinsOverTypePrefixAndLongerNames()
    {
        NodeDescriptor value = Create("value.constant", "Value", "Values", "Construct a constant.");
        NodeDescriptor merge = Create("control.valueMerge", "Value merge", "Control", "Forward.");
        NodeDescriptor array = Create("value.array", "Array", "Values", "Collect values.");
        IReadOnlyList<NodeDescriptor> matches = Find([array, merge, value], "Val");
        Assert.Equal([value.Type, merge.Type, array.Type], matches.Select(item => item.Type));
        foreach (ECatalogLayout layout in Enum.GetValues<ECatalogLayout>())
        {
            CatalogGroup group = Assert.Single(CatalogPresentation.Groups(matches, layout, item => item.DisplayName, "en", ranked: true));
            Assert.Equal(matches, group.Items);
        }
    }

    /// <summary>SubWorkflowという内部型・分類だけの一致は、名称のSub一致より後になります。</summary>
    [Fact]
    public void RecipeTypeAndCategoryDoNotOverrideNameMatches()
    {
        NodeDescriptor named = Create("extension.named", "Sub helper", "Data", "Run.");
        NodeDescriptor recipe = Create("subworkflow.call.abc", "A recipe", "SubWorkflows", "Run.");
        NodeDescriptor category = Create("extension.recipe", "B recipe", "SubWorkflows", "Run.");
        Assert.Equal([named.Type, recipe.Type, category.Type], Find([category, recipe, named], "Sub").Select(item => item.Type));
        IReadOnlyList<NodeDescriptor> translated = NodeQuickSearch.Find([recipe, named], "sub", null,
            item => string.Equals(item.Type, named.Type, StringComparison.Ordinal) ? "日本語の表示名"
                                                                                   : item.DisplayName, item => item.Description, "ja");
        Assert.Equal(named, translated[0]);
    }

    /// <summary>前の分類や説明文の一致より、入力した表示名の完全一致を優先します。</summary>
    [Fact]
    public void ExactNameAndTypeBeatDescriptionAcrossCategories()
    {
        NodeDescriptor condition = Create("control.condition", "Condition", "Control", "Compare or pass value when true.");
        NodeDescriptor pass = Create("data.pass", "Pass value", "Data", "Preserve the input.");
        NodeDescriptor prefix = Create("control.passing", "Pass value onward", "Control", "Forward.");
        NodeDescriptor[] catalog = [condition, prefix, pass];
        IReadOnlyList<NodeDescriptor> results = Find(catalog, "  PASS VALUE  ");
        Assert.Equal(new[] { pass.Type, prefix.Type, condition.Type }, results.Select(item => item.Type));
        Assert.Same(pass, Assert.Single(Find(catalog, "data.pass")));
    }

    /// <summary>語句変更時だけ最良候補へ戻し、矢印選択と空検索の境界を維持します。</summary>
    [Fact]
    public void FreshQueryResetsSelectionAndEmptyResultsCannotAddOldNode()
    {
        NodeDescriptor condition = Create("control.condition", "Condition", "Control", "Can pass value.");
        NodeDescriptor pass = Create("data.pass", "Pass value", "Data", "Preserve.");
        NodeDescriptor[] catalog = [condition, pass];
        IReadOnlyList<NodeDescriptor> matches = Find(catalog, "pass value");
        Assert.Same(pass, NodeQuickSearch.Select(matches, condition, true));
        Assert.Same(condition, NodeQuickSearch.Select(matches, condition, false));
        Assert.Null(NodeQuickSearch.Select(Find(catalog, "no such node"), pass, true));
        Assert.Same(condition, NodeQuickSearch.Select(Find(catalog, string.Empty), pass, true));
        Assert.Same(pass, NodeQuickSearch.Select(matches, null, false));
    }

    /// <summary>翻訳表示名を検索でき、選択分類の外へ追加候補を広げません。</summary>
    [Fact]
    public void LocalizedNameRanksFirstWithinExplicitCategory()
    {
        NodeDescriptor pass = Create("data.pass", "Pass value", "Data", "Preserve.");
        NodeDescriptor condition = Create("control.condition", "Condition", "Control", "値を通すこともできます。");
        NodeDescriptor[] catalog = [condition, pass];
        string Name(NodeDescriptor item)
        {
            if (ReferenceEquals(item, pass))
            {
                return "値を通す";
            }
            return item.DisplayName;
        }
        Assert.Same(pass, NodeQuickSearch.Find(catalog, "値を通す", null, Name, item => item.Description)[0]);
        Assert.Same(condition, Assert.Single(NodeQuickSearch.Find(catalog, "値を通す", "Control", Name, item => item.Description)));
    }

    /// <summary>表示とdescriptionの翻訳なしに順位を確認します。</summary>
    /// <returns>そのまま描画・上下選択に使う順序です。</returns>
    private static IReadOnlyList<NodeDescriptor> Find(IReadOnlyList<NodeDescriptor> catalog, string query) => NodeQuickSearch.Find(catalog, query, null, item => item.DisplayName, item => item.Description);

    /// <summary>検索に必要な表示契約だけを作ります。</summary>
    /// <returns>副作用のないdescriptorです。</returns>
    private static NodeDescriptor Create(string type, string name, string category, string description) => new(type, name, category, description, ENodeKind.Action, EExecutionLocation.Independent, [], [], [], JsonSerializer.Deserialize<JsonElement>("{}"));
}
