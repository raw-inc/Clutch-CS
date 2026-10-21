using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>カタログの分類・表示言語順・検索中の折り畳みを検証します。</summary>
public sealed class CatalogPresentationTests
{
    /// <summary>未実装ジャンルを空で表示し、隠したレシピを選択候補へ戻しません。</summary>
    [Fact]
    public void GenerationCategoriesCanBeEmptyAndHiddenRecipesStayHidden()
    {
        NodeDescriptor hidden = Node("old", "Old", "AI") with { IsHidden = true };
        IReadOnlyList<CatalogGroup> groups = CatalogPresentation.Groups([hidden], ECatalogLayout.Category, item => item.DisplayName, "en", includeEmpty: true);
        Assert.Equal(["TextGeneration", "VideoGeneration", "ImageGeneration"], groups.Select(group => group.Category));
        Assert.All(groups, group => Assert.Empty(group.Items));
        Assert.Empty(NodeQuickSearch.Find([hidden], string.Empty, null, item => item.DisplayName, item => item.Description));
    }

    /// <summary>APIの文字列またはJSONオブジェクトの両方を設定欄から編集できます。</summary>
    [Fact]
    public void UnionSettingsUseJsonEditorWithoutChangingNullableScalars()
    {
        Assert.Equal("json", SettingChoiceValues.EditorType(JsonSerializer.Deserialize<JsonElement>("{\"type\":[\"string\",\"object\"]}")));
        Assert.Equal("string", SettingChoiceValues.EditorType(JsonSerializer.Deserialize<JsonElement>("{\"type\":[\"string\",\"null\"]}")));
    }

    /// <summary>音声サービスを専用カテゴリへ集め、日英どちらでも処理先を識別できます。</summary>
    /// <returns>カテゴリ名と独立したノード名の確認です。</returns>
    [Fact]
    public async Task AudioProvidersHaveTheirOwnLocalizedCategoryAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        NodeDescriptor openAi = Node("ai.audio.generate", "original", "AudioGeneration");
        NodeDescriptor qwen = Node("ai.audio.qwen.generate", "original", "AudioGeneration");
        CatalogGroup group = Assert.Single(CatalogPresentation.Groups([qwen, openAi], ECatalogLayout.Category, text.NodeName, text.Language));
        Assert.Equal("Audio generation", group.Label);
        Assert.Equal(["OpenAI Speech", "Qwen3-TTS-VC"], group.Items.Select(text.NodeName));
        Assert.Equal("audio-generation", GraphPresentation.CategoryKey(group.Category));
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("音声生成", text[group.Label]);
        Assert.Equal("OpenAI Speech", text.NodeName(openAi));
        Assert.Equal("Qwen3-TTS-VC", text.NodeName(qwen));
        Assert.Equal("読み上げ言語", text.SettingName("languageType"));
    }

    /// <summary>値構築は実行寿命をActionのまま保ち、Sourceと一回処理から別表示します。</summary>
    [Fact]
    public void KindGroupsContainEachDescriptorOnceWithoutChangingRuntimeKind()
    {
        NodeDescriptor value = Node("value.constant", "Constant", "Values") with { IsValueOperation = true };
        NodeDescriptor source = Node("network.listen", "Listen", "Communication") with { Kind = ENodeKind.Source };
        NodeDescriptor action = Node("file.write", "Write", "File system");
        IReadOnlyList<CatalogGroup> groups = CatalogPresentation.Groups([action, source, value], ECatalogLayout.Kind, item => item.DisplayName, "en");
        Assert.Equal(["Values & types", "Continuous inputs (Source)", "Single operations (Action)"], groups.Select(group => group.Label));
        Assert.Same(value, Assert.Single(groups[0].Items));
        Assert.Same(source, Assert.Single(groups[1].Items));
        Assert.Same(action, Assert.Single(groups[2].Items));
        Assert.Equal(ENodeKind.Action, value.Kind);
    }

    /// <summary>カテゴリ文字列ではなく共通メタデータだけを種類とアイコンへ反映します。</summary>
    [Fact]
    public void ValueFlagKeepsOriginalCategoryAndDoesNotGuessExtensions()
    {
        NodeDescriptor data = Node("data.convert", "Convert", "Data") with { IsValueOperation = true };
        NodeDescriptor math = Node("math.add", "Add", "Math") with { IsValueOperation = true };
        NodeDescriptor extension = Node("custom", "Custom", "Values");
        Assert.Equal("values", CatalogPresentation.Icon(data));
        Assert.Equal("values", CatalogPresentation.Icon(math));
        Assert.Equal("nodes", CatalogPresentation.Icon(extension));
        Assert.Equal("Data", data.Category);
        Assert.Equal("Math", math.Category);
        Assert.Equal(ENodeKind.Action, math.Kind);
        IReadOnlyList<CatalogGroup> groups = CatalogPresentation.Groups([data, math, extension], ECatalogLayout.Kind, item => item.DisplayName, "en");
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Same(extension, Assert.Single(groups[1].Items));
    }

    /// <summary>OS文化が異なっても表示中の言語の文字照合を使い、同名はTypeIdで固定します。</summary>
    [Fact]
    public void NameLayoutUsesDisplayCultureAndStableTypeIdTies()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            NodeDescriptor[] catalog = [Node("z", "Zulu"), Node("tie.b", "äther"), Node("tie.a", "äther"), Node("a", "Alpha")];
            CatalogGroup group = Assert.Single(CatalogPresentation.Groups(catalog, ECatalogLayout.Name, item => item.DisplayName, "en"));
            Assert.Equal(["a", "tie.a", "tie.b", "z"], group.Items.Select(item => item.Type));
            CatalogGroup swedish = Assert.Single(CatalogPresentation.Groups(catalog, ECatalogLayout.Name, item => item.DisplayName, "sv"));
            Assert.Equal(["a", "z", "tie.a", "tie.b"], swedish.Items.Select(item => item.Type));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>日本語表示では元の英語DisplayNameではなく、見えている名前を並べます。</summary>
    [Fact]
    public void JapaneseNamesAndQuickPickerUseTheSameOrder()
    {
        NodeDescriptor[] catalog = [Node("a", "Alpha"), Node("z", "Zulu"), Node("tie", "Other")];
        string Name(NodeDescriptor item) => item.Type switch { "a" => "わかめ", "z" => "あさがお", _ => "あさがお" };
        IReadOnlyList<NodeDescriptor> ordered = Assert.Single(CatalogPresentation.Groups(catalog, ECatalogLayout.Name, Name, "ja")).Items;
        Assert.Equal(["tie", "z", "a"], ordered.Select(item => item.Type));
        Assert.Equal(ordered.Select(item => item.Type), NodeQuickSearch.Find(catalog, string.Empty, null, Name, item => item.Description, "ja").Select(item => item.Type));
    }

    /// <summary>検索は折り畳み状態を消さずに一致を開き、消去後に元の状態へ戻ります。</summary>
    [Fact]
    public void SearchTemporarilyExpandsMatchesAndKeepsLayoutSpecificFoldState()
    {
        NodeDescriptor first = Node("value.constant", "Constant", "Values");
        NodeDescriptor second = Node("file.write", "Write", "File system");
        HashSet<string> collapsed = new(StringComparer.Ordinal) { "category:Values", "kind:2" };
        IReadOnlyList<NodeDescriptor> filtered = NodeQuickSearch.Find([first, second], "constant", null, item => item.DisplayName, item => item.Description, "en");
        CatalogGroup match = Assert.Single(CatalogPresentation.Groups(filtered, ECatalogLayout.Category, item => item.DisplayName, "en"));
        Assert.Same(first, Assert.Single(match.Items));
        Assert.False(CatalogPresentation.IsExpanded(match.Key, string.Empty, collapsed));
        Assert.True(CatalogPresentation.IsExpanded(match.Key, "constant", collapsed));
        Assert.False(CatalogPresentation.IsExpanded(match.Key, "  ", collapsed));
        Assert.False(CatalogPresentation.IsExpanded("kind:2", string.Empty, collapsed));
        Assert.True(CatalogPresentation.IsExpanded("kind:0", string.Empty, collapsed));
        Assert.Equal(2, collapsed.Count);
    }

    /// <summary>表示言語順を導入しても完全一致が説明文一致に負けません。</summary>
    [Fact]
    public void QuickSearchStillRanksExactNamesAndRetainsKeyboardSelection()
    {
        NodeDescriptor exact = Node("z.exact", "Zulu");
        NodeDescriptor description = Node("a.description", "Alpha") with { Description = "Zulu" };
        IReadOnlyList<NodeDescriptor> matches = NodeQuickSearch.Find([description, exact], "Zulu", null, item => item.DisplayName, item => item.Description, "ja");
        Assert.Same(exact, matches[0]);
        Assert.Same(exact, NodeQuickSearch.Select(matches, description, true));
        Assert.Same(description, NodeQuickSearch.Select(matches, description, false));
    }

    /// <summary>追加分類名を日本語でも自然に表示し、Mathは利用者指定の共通表記です。</summary>
    /// <returns>辞書を実際に読み込む確認です。</returns>
    [Fact]
    public async Task GroupLabelsSwitchLanguageAndKeepMathName()
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        Assert.Equal("By category", ui["By category"]);
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("カテゴリ別", ui["By category"]);
        Assert.Equal("値・型", ui["Values & types"]);
        Assert.Equal("Math", ui[GraphPresentation.CategoryName("Math")]);
    }

    /// <summary>表示順のための副作用を持たない記述子です。</summary>
    /// <returns>指定したカテゴリと名前のノードです。</returns>
    private static NodeDescriptor Node(string type, string name, string category = "Data") => new(type, name, category, string.Empty, ENodeKind.Action, EExecutionLocation.Independent, [], [], [], JsonSerializer.SerializeToElement(new { }));
}
