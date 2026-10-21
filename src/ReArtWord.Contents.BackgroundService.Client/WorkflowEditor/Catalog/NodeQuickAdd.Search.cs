using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;

/// <summary>ノード検索の候補絞込みとキーボード選択を処理します。</summary>
public sealed partial class NodeQuickAdd
{
    /// <summary>ノード検索の移動・確定・取消キーを処理します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [JSInvokable, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task HandleKeyAsync(string key, string? currentQuery = null)
    {
        if (!open || disposed)
        {
            return;
        }

        if (currentQuery is not null && !string.Equals(search, currentQuery, StringComparison.Ordinal))
        {
            SetSearch(currentQuery);
        }

        if (string.Equals(key, EscapeKey, StringComparison.Ordinal) || string.Equals(key, TabKey, StringComparison.Ordinal))
        {
            await CloseAsync().ConfigureAwait(true);
            return;
        }

        if (string.Equals(key, EnterKey, StringComparison.Ordinal))
        {
            if (selected is not null)
            {
                await AddAsync(selected).ConfigureAwait(true);
            }

            return;
        }

        if (string.Equals(key, MatchArrowLeft, StringComparison.Ordinal) || string.Equals(key, ArrowRightKey, StringComparison.Ordinal))
        {
            string?[] categories = [null, ..Categories];
            int current = Array.IndexOf(categories, category);
            int direction = string.Equals(key, MatchArrowLeft, StringComparison.Ordinal) ? -1
                                                                                         : 1;
            CategoryChanged(categories[Math.Clamp(current + direction, 0, categories.Length - 1)]);
        }

        if (matches.Count != 0 && (string.Equals(key, MatchArrowUp, StringComparison.Ordinal) || string.Equals(key, ArrowDownKey, StringComparison.Ordinal)))
        {
            int current = selected is null ? -1
                                           : matches.ToList().FindIndex(item => string.Equals(item.Type, selected.Type, StringComparison.Ordinal));
            int direction = string.Equals(key, MatchArrowUp, StringComparison.Ordinal) ? -1
                                                                                       : 1;
            selected = matches[Math.Clamp(current + direction, 0, matches.Count - 1)];
        }

        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    /// <summary>検索入力の変更を候補絞込みへ反映します。</summary>
    private void SearchChanged(ChangeEventArgs args)
    {
        SetSearch(args.Value?.ToString() ?? string.Empty);
    }

    /// <summary>検索文字列を更新して候補を再計算します。</summary>
    private void SetSearch(string value)
    {
        bool changed = !string.Equals(search, value, StringComparison.Ordinal);
        search = value;
        if (changed)
        {
            selected = null;
        }

        RefreshMatches(changed);
    }

    /// <summary>カテゴリ選択を変更して候補を再計算します。</summary>
    private void CategoryChanged(string? value)
    {
        category = value;
        selected = null;
        RefreshMatches(true);
    }

    /// <summary>現在の検索条件から有限数の追加候補を作ります。</summary>
    private void RefreshMatches(bool queryChanged = false)
    {
        matches = NodeQuickSearch.Find(Catalog, search, category, Ui.NodeName, Ui.NodeDescription, Ui.Language);
        selected = NodeQuickSearch.Select(matches, selected, queryChanged);
    }
}
