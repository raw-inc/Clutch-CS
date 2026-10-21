using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Regions;

/// <summary>実行とエディタが共有する、反復・同時実行制限の区間対応です。</summary>
public sealed class WorkflowRegionMap
{
    /// <summary>構築済みの区間と診断を同じ結果として公開します。</summary>
    private WorkflowRegionMap(IReadOnlyList<WorkflowRegion> regions, IReadOnlyList<ValidationIssue> issues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> inherited, IReadOnlySet<string> scopedValueMerges)
    {
        Regions = regions;
        Issues = issues;
        Inherited = inherited;
        ScopedValueMerges = scopedValueMerges;
    }

    /// <summary>開始・終了と所属ノードです。</summary>
    public IReadOnlyList<WorkflowRegion> Regions
    {
        get;
    }

    /// <summary>開始と終了の対応付けの問題です。</summary>
    public IReadOnlyList<ValidationIssue> Issues
    {
        get;
    }

    /// <summary>レシピの内部入口へ引き継ぐ公開入力です。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Inherited
    {
        get;
    }

    /// <summary>別セッションへ分離せず、区間内の到着値を合流するノードです。</summary>
    public IReadOnlySet<string> ScopedValueMerges
    {
        get;
    }

    /// <summary>保存前の未完成な接続でも、識別できるノードだけで区間を組み立てます。</summary>
    /// <returns>実行・表示で共通の範囲と対応付けの問題です。</returns>
    public static WorkflowRegionMap Create(WorkflowDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<string>>? recipeInputs = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Dictionary<string, NodeDefinition> nodes = definition.Nodes.DistinctBy(node => node.Id, StringComparer.Ordinal).ToDictionary(node => node.Id, StringComparer.Ordinal);
        WireDefinition[] wires = definition.Wires.Where(wire => nodes.ContainsKey(wire.FromNode) && nodes.ContainsKey(wire.ToNode)).ToArray();
        WorkflowRegionMembers membership = new(nodes, wires, recipeInputs);
        List<WorkflowRegionMembers.Region> regions = [];
        List<ValidationIssue> issues = [];
        string success = "success";
        foreach (NodeDefinition start in nodes.Values.Where(node => WorkflowRegionNodeTypes.IsStart(node.Type)))
        {
            string pair = WorkflowRegionNodeTypes.PairPort(start.Type);
            WireDefinition[] links = wires.Where(wire => string.Equals(wire.FromNode, start.Id, StringComparison.Ordinal) && string.Equals(wire.FromPort, pair, StringComparison.Ordinal)).ToArray();
            if (1 < links.Length || links.Any(wire => !WorkflowRegionNodeTypes.IsPair(start.Type, nodes[wire.ToNode].Type)
                || !string.Equals(wire.ToPort, pair, StringComparison.Ordinal) || wire.Path is not null || !string.Equals(wire.When, success, StringComparison.Ordinal)))
            {
                string code = ConcurrencyNodeTypes.IsStart(start.Type) ? "concurrency.pair"
                                                                       : "iteration.pair";
                issues.Add(new(code, "Connect the scope port directly to one matching End, or leave it unconnected.", start.Id));
                continue;
            }
            NodeDefinition? end = null;
            if (links.Length == 1)
            {
                end = nodes[links[0].ToNode];
            }
            WorkflowRegionMembers.Region region = new(start, end, [start.Id]);
            List<string> pending = wires.Where(wire => string.Equals(wire.FromNode, start.Id, StringComparison.Ordinal)
                && WorkflowRegionNodeTypes.IsBodyPort(start.Type, wire.FromPort)).Select(wire => wire.ToNode).ToList();
            if (end is not null)
            {
                pending.Add(end.Id);
            }
            membership.Expand(region, pending);
            regions.Add(region);
        }
        membership.BindValueMerges(regions);
        foreach (NodeDefinition end in nodes.Values.Where(node => WorkflowRegionNodeTypes.IsEnd(node.Type)))
        {
            if (regions.Count(region => string.Equals(region.End?.Id, end.Id, StringComparison.Ordinal)) != 1)
            {
                string code = ConcurrencyNodeTypes.IsEnd(end.Type) ? "concurrency.end"
                                                                   : "iteration.end";
                issues.Add(new(code, "Connect this End directly to one matching start scope output.", end.Id));
            }
        }
        return new(regions.Select(region => new WorkflowRegion(region.Start, region.End, region.Members)).ToArray(), issues,
            membership.Inherited.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal), membership.ScopedValueMerges);
    }

    /// <summary>開始・終了は自身の区間、内部は最も内側、最上位は全体として表示します。</summary>
    /// <returns>選択ノードに対応する区間です。nullなら最上位です。</returns>
    public WorkflowRegion? Find(string nodeId)
        => Regions.FirstOrDefault(region => string.Equals(region.Start.Id, nodeId, StringComparison.Ordinal) || string.Equals(region.End?.Id, nodeId, StringComparison.Ordinal))
            ?? Regions.Where(region => region.Members.Contains(nodeId)).MinBy(region => region.Members.Count);
}
