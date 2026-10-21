using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>同じクラスタ・Peer画面内の一覧外草稿への入口を保持します。</summary>
internal sealed class EditorWorkflowTabState
{
    private readonly Dictionary<string, EditorWorkflowTab> retained = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    private readonly HashSet<string> owned = new(StringComparer.Ordinal);

    internal IReadOnlyList<EditorWorkflowTab> All => order.Where(retained.ContainsKey).Select(id => retained[id]).ToArray();

    /// <summary>表示順と非表示を、本文を含めずブラウザーへ保存できる形にします。</summary>
    /// <returns>同じ所有範囲のタブ設定です。</returns>
    internal EditorWorkflowTabLayout Export() => new(order.ToArray(), hidden.ToArray(), All);

    /// <summary>保存された表示順・非表示状態と、一覧外の草稿への入口を復元します。</summary>
    internal void Restore(EditorWorkflowTabLayout layout)
    {
        retained.Clear();
        owned.Clear();
        order.Clear();
        order.AddRange(layout.Order.Distinct(StringComparer.Ordinal));
        hidden.Clear();
        hidden.UnionWith(layout.Hidden);
        foreach (EditorWorkflowTab tab in layout.Tabs)
        {
            retained[tab.Id] = tab;
        }
    }

    /// <summary>同じ画面所有者が復元できる本文だけを、未保存タブとして引き継ぎます。</summary>
    internal void Recover(IReadOnlyList<EditorDraft> drafts)
    {
        foreach (EditorDraft draft in drafts)
        {
            WorkflowDefinition definition = draft.Document;
            owned.Add(definition.Id);
            retained[definition.Id] = new(definition.Id, definition.Name, draft.Dirty, draft.LocalOnly, SubworkflowInterface.IsSubworkflow(definition), definition.IsDashboard);
        }
    }

    /// <summary>オンラインで一覧を読み切ったときだけ、本文への入口がない表示情報を除きます。</summary>
    /// <returns>本文・別画面の草稿を変更せず、タブ情報を整理したかです。</returns>
    internal bool Prune(IReadOnlyList<WorkflowListSummary> items, string selectedId, bool complete)
    {
        if (!complete || items.Any(item => item.Id is null))
        {
            return false;
        }
        HashSet<string?> saved = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        string[] missing = retained.Keys.Where(id => !string.Equals(id, selectedId, StringComparison.Ordinal) && !owned.Contains(id) && !saved.Contains(id)).ToArray();
        foreach (string id in missing)
        {
            Forget(id);
        }
        return missing.Length != 0;
    }

    /// <summary>WF本体と草稿を保持したままタブだけを隠します。</summary>
    internal void Hide(string id) => hidden.Add(id);

    /// <summary>取得済み一覧から明示選択したタブを再表示します。</summary>
    internal void Reveal(string id) => hidden.Remove(id);

    /// <summary>現在の表示順から隣のタブを取得します。</summary>
    /// <returns>非表示を除いた移動先、端ならnullです。</returns>
    internal string? Neighbor(string? id, int offset)
    {
        string[] visible = order.Where(key => !hidden.Contains(key) && retained.ContainsKey(key)).ToArray();
        int index = Array.FindIndex(visible, key => string.Equals(key, id, StringComparison.Ordinal));
        int target = index + offset;
        if (0 <= index && 0 <= target && target < visible.Length)
        {
            return visible[target];
        }
        return null;
    }

    /// <summary>削除済みのタブも扱い、残っている可視タブへの入口を維持します。</summary>
    /// <returns>右隣、左隣、既に一覧から消えた場合は先頭です。</returns>
    internal string? NextVisible(string id)
    {
        string[] visible = order.Where(key => !hidden.Contains(key) && retained.ContainsKey(key)).ToArray();
        int index = Array.FindIndex(visible, key => string.Equals(key, id, StringComparison.Ordinal));
        return visible.Skip(index + 1).Concat(visible.Take(Math.Max(index, 0)).Reverse()).FirstOrDefault(key => !string.Equals(key, id, StringComparison.Ordinal));
    }

    /// <summary>移動先の前後へ入れ替え、非表示タブの位置は保ちます。</summary>
    internal void Move(string id, string target)
    {
        int from = order.IndexOf(id), to = order.IndexOf(target);
        if (from < 0 || to < 0 || from == to || hidden.Contains(id) || hidden.Contains(target))
        {
            return;
        }
        order.RemoveAt(from);
        order.Insert(to, id);
    }

    /// <summary>明示削除した草稿への入口だけを取り除きます。</summary>
    internal void Forget(string id)
    {
        retained.Remove(id);
        order.Remove(id);
        hidden.Remove(id);
        owned.Remove(id);
    }

    /// <summary>分離で作られた未保存レシピにもタブから戻れます。</summary>
    internal void Remember(WorkflowDefinition definition)
    {
        owned.Add(definition.Id);
        retained[definition.Id] = new(definition.Id, definition.Name, true, false, true);
    }

    /// <summary>現在の草稿と観測一覧をまとめ、次ページで未保存tabを失いません。</summary>
    /// <returns>本文は元Editorに残したままの表示用tabです。</returns>
    internal IReadOnlyList<EditorWorkflowTab> Update(IReadOnlyList<WorkflowListSummary> items, string id, string name, bool dirty, bool localOnly, bool isSubworkflow = false, bool isDashboard = false)
    {
        if (!string.IsNullOrEmpty(id))
        {
            owned.Add(id);
            retained[id] = new(id, name, dirty, localOnly, isSubworkflow, isDashboard);
        }
        List<EditorWorkflowTab> tabs = [];
        foreach (WorkflowListSummary item in items)
        {
            if (item.Id is not null)
            {
                EditorWorkflowTab tab = owned.Contains(item.Id) ? retained[item.Id]
                                                                : new(item.Id, item.Name, false, item.LocalOnly, item.IsSubworkflow, item.IsDashboard);
                if (!tab.Dirty && !string.Equals(item.Id, id, StringComparison.Ordinal))
                {
                    tab = tab with { Name = item.Name, LocalOnly = item.LocalOnly, IsSubworkflow = item.IsSubworkflow, IsDashboard = item.IsDashboard };
                }
                retained[item.Id] = tab;
                tabs.Add(tab);
            }
        }
        tabs.AddRange(retained.Values.Where(tab => tabs.All(item => !string.Equals(item.Id, tab.Id, StringComparison.Ordinal))));
        HashSet<string> known = new(order, StringComparer.Ordinal);
        foreach (EditorWorkflowTab tab in tabs)
        {
            if (known.Add(tab.Id))
            {
                if (tab.IsDashboard)
                {
                    int index = 0;
                    while (index < order.Count && retained.GetValueOrDefault(order[index])?.IsDashboard == true)
                    {
                        index++;
                    }
                    order.Insert(index, tab.Id);
                }
                else
                {
                    order.Add(tab.Id);
                }
            }
        }
        return order.Where(key => !hidden.Contains(key) && retained.ContainsKey(key)).Select(key => retained[key]).ToArray();
    }
}
