using System.Globalization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>カタログとクイック追加の表示言語に対応した名前順を共有します。</summary>
internal static class CatalogPresentation
{
    /// <summary>UIの表示言語を使い、実行OSの英語順へ固定しません。</summary>
    /// <returns>指定文化の表示名比較です。</returns>
    internal static StringComparer NameComparer(string? language) => StringComparer.Create(CultureInfo.GetCultureInfo(language ?? CultureInfo.CurrentUICulture.Name), true);

    /// <summary>分類を変えても各項目を一度だけ含む表示グループを作ります。</summary>
    /// <returns>同名はTypeIdで安定させた一覧です。</returns>
    internal static IReadOnlyList<CatalogGroup> Groups(IEnumerable<NodeDescriptor> catalog, ECatalogLayout layout, Func<NodeDescriptor, string> name, string language, bool includeEmpty = false, bool ranked = false)
    {
        if (ranked)
        {
            return [new("matches", "Search results", string.Empty, catalog.Where(item => !item.IsHidden).ToArray())];
        }
        StringComparer comparer = NameComparer(language);
        NodeDescriptor[] ordered = catalog.Where(item => !item.IsHidden).OrderBy(name, comparer).ThenBy(item => item.Type, StringComparer.Ordinal).ToArray();
        if (layout == ECatalogLayout.Name)
        {
            return [new("names", "All nodes", string.Empty, ordered)];
        }
        if (layout == ECatalogLayout.Kind)
        {
            return ordered.GroupBy(Kind).OrderBy(group => group.Key).Select(group => new CatalogGroup("kind:" + group.Key.ToString(CultureInfo.InvariantCulture), KindLabel(group.Key), string.Empty, group.ToArray())).ToArray();
        }
        List<CatalogGroup> groups = ordered.GroupBy(item => item.Category, StringComparer.Ordinal)
            .Select(group => new CatalogGroup("category:" + group.Key, GraphPresentation.CategoryName(group.Key), group.Key, group.ToArray())).ToList();
        foreach (string category in new[] { "TextGeneration", "VideoGeneration", "ImageGeneration" })
        {
            if (includeEmpty && groups.All(group => !string.Equals(group.Category, category, StringComparison.Ordinal)))
            {
                groups.Add(new("category:" + category, GraphPresentation.CategoryName(category), category, []));
            }
        }
        return groups.OrderBy(group => GraphPresentation.CategoryOrder(group.Category)).ThenBy(group => group.Category, StringComparer.Ordinal).ToArray();
    }

    /// <summary>検索中だけ一致項目を開き、保存した折り畳み操作は変更しません。</summary>
    /// <returns>この描画でグループを開くかです。</returns>
    internal static bool IsExpanded(string key, string search, IReadOnlySet<string> collapsed) => !string.IsNullOrWhiteSpace(search) || !collapsed.Contains(key);

    /// <summary>実行契約を変更せず値・型だけ専用アイコンで区別します。</summary>
    /// <returns>共通SVGアイコン名です。</returns>
    internal static string Icon(NodeDescriptor item) => Kind(item) switch
    {
        0 => "values",
        1 => "play",
        _ => "nodes",
    };

    /// <summary>項目と選択詳細で同じ種類名を使います。</summary>
    /// <returns>翻訳可能な短い種類名です。</returns>
    internal static string ItemKindLabel(NodeDescriptor item) => Kind(item) switch
    {
        0 => "Values & types",
        1 => "Source",
        _ => "Action",
    };

    /// <summary>値構築はActionの寿命を変えず、表示グループだけ区別します。</summary>
    /// <returns>値構築0、Source1、Action2です。</returns>
    private static int Kind(NodeDescriptor item)
    {
        if (item.IsValueOperation)
        {
            return 0;
        }
        if (item.Kind == ENodeKind.Source)
        {
            return 1;
        }
        return 2;
    }

    /// <summary>表示分類の自然言語キーを返します。</summary>
    /// <returns>翻訳可能な見出しです。</returns>
    private static string KindLabel(int kind) => kind switch
    {
        0 => "Values & types",
        1 => "Continuous inputs (Source)",
        _ => "Single operations (Action)",
    };
}
