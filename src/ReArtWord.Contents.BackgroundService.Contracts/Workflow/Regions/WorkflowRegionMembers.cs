using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Regions;

/// <summary>反復の到達範囲と、同じ要素へ戻る値合流の所有先を確定します。</summary>
internal sealed class WorkflowRegionMembers
{
    private readonly IReadOnlyDictionary<string, NodeDefinition> nodes;
    private readonly ILookup<string, WireDefinition> outgoing;
    private readonly ILookup<string, WireDefinition> incoming;
    private readonly ILookup<string, string> entries;

    /// <summary>保存配線とレシピ入力を同じ到達判定へ渡します。</summary>
    internal WorkflowRegionMembers(IReadOnlyDictionary<string, NodeDefinition> definitions, IReadOnlyList<WireDefinition> wires, IReadOnlyDictionary<string, IReadOnlyList<string>>? recipeInputs)
    {
        nodes = definitions;
        outgoing = wires.ToLookup(wire => wire.FromNode, StringComparer.Ordinal);
        incoming = wires.ToLookup(wire => wire.ToNode, StringComparer.Ordinal);
        entries = (recipeInputs ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)).SelectMany(pair => pair.Value.Select(input => (Input: input, Entry: pair.Key))).ToLookup(pair => pair.Input, pair => pair.Entry, StringComparer.Ordinal);
    }

    /// <summary>レシピ内部入口へ引き継ぐ、反復内の公開入力です。</summary>
    internal Dictionary<string, HashSet<string>> Inherited
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>外部へ事象を分離せず、同じ要素内で先着値を選ぶ合流です。</summary>
    internal HashSet<string> ScopedValueMerges
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>Endと独立した事象境界で止まり、同じ範囲の後続だけを取り込みます。</summary>
    internal void Expand(Region region, IEnumerable<string> seeds)
    {
        Queue<string> pending = new(seeds);
        while (pending.TryDequeue(out string? id))
        {
            if (EventMergePorts.IsBoundary(nodes[id]) && !ScopedValueMerges.Contains(id))
            {
                continue;
            }
            if (!region.Members.Add(id) || string.Equals(id, region.End?.Id, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (WireDefinition wire in outgoing[id])
            {
                pending.Enqueue(wire.ToNode);
            }
            foreach (string entry in entries[id])
            {
                if (!Inherited.TryGetValue(entry, out HashSet<string>? inputs))
                {
                    Inherited[entry] = inputs = new(StringComparer.Ordinal);
                }
                inputs.Add(id);
                pending.Enqueue(entry);
            }
        }
    }

    /// <summary>全到着元が同じ要素に属し、その要素へ戻る合流だけを反復内へ含めます。</summary>
    internal void BindValueMerges(IReadOnlyList<Region> regions)
    {
        List<NodeDefinition> pending = nodes.Values.Where(node => ValueMergePorts.IsNode(node.Type)).ToList();
        bool changed;
        do
        {
            changed = false;
            foreach (NodeDefinition node in pending.ToArray())
            {
                WireDefinition[] inputs = incoming[node.Id].Where(wire => 0 < ConcatPorts.Index(wire.ToPort)).ToArray();
                if (inputs.Length == 0)
                {
                    continue;
                }
                Region? owner = SourceScope(inputs[0], regions);
                if (owner is null || inputs.Any(wire => !ReferenceEquals(SourceScope(wire, regions), owner)) || !Rejoins(node.Id, owner))
                {
                    continue;
                }
                ScopedValueMerges.Add(node.Id);
                pending.Remove(node);
                foreach (Region region in regions.Where(region => region.Members.Contains(owner.Start.Id)))
                {
                    Expand(region, [node.Id]);
                }
                changed = true;
            }
        } while (changed);
    }

    /// <summary>内側Endの集約値と、内側の一要素の値を区別します。</summary>
    /// <returns>値を供給する最も内側の反復、または反復外です。</returns>
    private static Region? SourceScope(WireDefinition wire, IReadOnlyList<Region> regions)
        => regions.Where(region => region.Members.Contains(wire.FromNode)
            && !string.Equals(region.End?.Id, wire.FromNode, StringComparison.Ordinal)
            && (!string.Equals(region.Start.Id, wire.FromNode, StringComparison.Ordinal) || WorkflowRegionNodeTypes.IsBodyPort(region.Start.Type, wire.FromPort)))
            .MinBy(region => region.Members.Count);

    /// <summary>連続する値合流も調べ、ループ外へ配送するだけの既存経路は取り込みません。</summary>
    /// <returns>同じ反復内の処理またはEndへ戻る経路があるかです。</returns>
    private bool Rejoins(string node, Region region)
    {
        Queue<string> pending = new(outgoing[node].Select(wire => wire.ToNode).Concat(entries[node]));
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (pending.TryDequeue(out string? id))
        {
            if (!visited.Add(id))
            {
                continue;
            }
            if (region.Members.Contains(id))
            {
                return true;
            }
            if (EventMergePorts.IsBoundary(nodes[id]) && !ValueMergePorts.IsNode(nodes[id].Type))
            {
                continue;
            }
            foreach (string next in outgoing[id].Select(wire => wire.ToNode).Concat(entries[id]))
            {
                pending.Enqueue(next);
            }
        }
        return false;
    }

    /// <summary>構築中だけ変更する区間の所属集合です。</summary>
    /// <param name="Start">開始ノード。</param>
    /// <param name="End">任意の終了ノード。</param>
    /// <param name="Members">構築中の所属ノード。</param>
    internal sealed record Region(NodeDefinition Start, NodeDefinition? End, HashSet<string> Members);
}
