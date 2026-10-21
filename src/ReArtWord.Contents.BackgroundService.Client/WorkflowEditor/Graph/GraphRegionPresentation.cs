using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Regions;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>配置や実行表示の更新で区間解析を繰り返さず、数値の表示用識別だけをSVGへ渡します。</summary>
internal sealed class GraphRegionPresentation
{
    private WorkflowDefinition? previous;
    private readonly Dictionary<string, string> selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> membership = new(StringComparer.Ordinal);

    /// <summary>トポロジーが変化したときだけ、実行と共通の区間判定を再構築します。</summary>
    internal void Update(WorkflowDefinition definition)
    {
        if (ReferenceEquals(previous, definition))
        {
            return;
        }
        bool unchanged = previous is not null && string.Equals(previous.Id, definition.Id, StringComparison.Ordinal)
            && previous.Wires.SequenceEqual(definition.Wires) && previous.Nodes.Select(node => (node.Id, node.Type)).SequenceEqual(definition.Nodes.Select(node => (node.Id, node.Type)));
        previous = definition;
        if (unchanged)
        {
            return;
        }
        selected.Clear();
        membership.Clear();
        Dictionary<string, List<string>> groups = new(StringComparer.Ordinal);
        WorkflowRegion[] regions = WorkflowRegionMap.Create(definition).Regions.OrderBy(region => region.Members.Count).ToArray();
        for (int index = 0; index < regions.Length; index++)
        {
            string id = (index + 1).ToString(CultureInfo.InvariantCulture);
            foreach (string member in regions[index].Members)
            {
                selected.TryAdd(member, id);
                if (!groups.TryGetValue(member, out List<string>? contained))
                {
                    groups[member] = contained = [];
                }
                contained.Add(id);
            }
        }
        for (int index = 0; index < regions.Length; index++)
        {
            string id = (index + 1).ToString(CultureInfo.InvariantCulture);
            selected[regions[index].Start.Id] = id;
            if (regions[index].End is { } end)
            {
                selected[end.Id] = id;
            }
        }
        foreach ((string node, List<string> contained) in groups)
        {
            membership[node] = string.Join(' ', contained);
        }
    }

    /// <summary>選択したときに表示する区間を返します。</summary>
    /// <returns>最上位は0、その他は表示用識別子です。</returns>
    internal string SelectedScope(string node) => selected.GetValueOrDefault(node) ?? "0";

    /// <summary>入れ子を含む所属区間を返します。</summary>
    /// <returns>空白で区切った表示用識別子です。</returns>
    internal string? Membership(string node) => membership.GetValueOrDefault(node);
}
