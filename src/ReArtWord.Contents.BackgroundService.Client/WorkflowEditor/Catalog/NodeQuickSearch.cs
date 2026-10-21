using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>クイック追加の表示順と選択順を同じ検索結果に揃えます。</summary>
internal static class NodeQuickSearch
{
    /// <summary>表示名・型の完全一致を説明文より先に表示します。</summary>
    /// <returns>画面とキー操作で共用する順位付き候補です。</returns>
    internal static IReadOnlyList<NodeDescriptor> Find(IEnumerable<NodeDescriptor> catalog, string query, string? category, Func<NodeDescriptor, string> name, Func<NodeDescriptor, string> description, string? language = null)
    {
        string text = query.Trim();
        return catalog.Where(item => !item.IsHidden && (category is null || string.Equals(item.Category, category, StringComparison.Ordinal)))
            .Select(item => (Item: item, Rank: Rank(item, text, name(item), description(item))))
            .Where(item => item.Rank != int.MaxValue)
            .OrderBy(item => item.Rank)
            .ThenBy(item => text.Length == 0 ? 0
                                             : MatchLength(item.Item, text, name(item.Item)))
            .ThenBy(item => GraphPresentation.CategoryOrder(item.Item.Category))
            .ThenBy(item => item.Item.Category, StringComparer.Ordinal)
            .ThenBy(item => name(item.Item), CatalogPresentation.NameComparer(language))
            .ThenBy(item => item.Item.Type, StringComparer.Ordinal)
            .Select(item => item.Item).ToArray();
    }

    /// <summary>検索語変更では旧選択を引き継がず、同じ検索の再描画だけ選択を維持します。</summary>
    /// <returns>最良候補または明示的な選択、空候補ならnullです。</returns>
    internal static NodeDescriptor? Select(IReadOnlyList<NodeDescriptor> matches, NodeDescriptor? previous, bool queryChanged)
    {
        if (!queryChanged && previous is not null)
        {
            NodeDescriptor? retained = matches.FirstOrDefault(item => string.Equals(item.Type, previous.Type, StringComparison.Ordinal));
            if (retained is not null)
            {
                return retained;
            }
        }
        if (matches.Count == 0)
        {
            return null;
        }
        return matches[0];
    }

    /// <summary>名称一致の具体性に応じた順位を返します。</summary>
    /// <returns>現在の表示名、元の名称、内部型、説明・分類の順に一致を優先します。</returns>
    private static int Rank(NodeDescriptor item, string query, string name, string description)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        int visible = NameRank(name, query);
        if (visible != int.MaxValue)
        {
            return visible;
        }
        int original = NameRank(item.DisplayName, query);
        if (original != int.MaxValue)
        {
            return 3 + original;
        }
        int type = NameRank(item.Type, query);
        if (type != int.MaxValue)
        {
            return 6 + type;
        }
        if (description.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase) || GraphPresentation.CategoryName(item.Category).Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 9;
        }
        return int.MaxValue;
    }

    /// <summary>一つの名称内では完全・前方・部分一致を順に扱います。</summary>
    /// <returns>一致段階、または不一致です。</returns>
    private static int NameRank(string value, string query)
    {
        if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (value.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        return int.MaxValue;
    }

    /// <summary>同じ一致段階では余分な文字が少ない名称を先にします。</summary>
    /// <returns>実際に一致した表示名、または内部型の長さです。</returns>
    private static int MatchLength(NodeDescriptor item, string query, string name)
    {
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return name.Length;
        }
        return item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ? item.DisplayName.Length
                                                                                    : item.Type.Length;
    }
}
