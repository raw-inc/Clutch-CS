using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>反復の入れ子を、親から見た一つの依存単位へ対応させます。</summary>
/// <param name="Regions">交差のない固定範囲。</param>
internal sealed record IterationPlan(IReadOnlyList<IterationRegion> Regions)
{
    /// <summary>指定範囲の直下にある反復だけを取得します。</summary>
    /// <returns>互いに交差しない直下の反復です。</returns>
    internal IReadOnlyList<IterationRegion> Children(HashSet<string> members, string? activeStart)
    {
        IterationRegion[] contained = Regions.Where(region => !string.Equals(region.Start.Id, activeStart, StringComparison.Ordinal) && region.Members.IsSubsetOf(members)).ToArray();
        return contained.Where(region => !contained.Any(other => !ReferenceEquals(region, other) && region.Members.IsProperSubsetOf(other.Members))).ToArray();
    }

    /// <summary>各ノードをその階層の有限実行単位へ写します。</summary>
    /// <returns>通常ノード自身、または直下の反復開始識別子です。</returns>
    internal Dictionary<string, string> Owners(HashSet<string> members, string? activeStart)
    {
        Dictionary<string, string> owners = members.ToDictionary(id => id, StringComparer.Ordinal);
        foreach (IterationRegion region in Children(members, activeStart))
        {
            foreach (string member in region.Members)
            {
                owners[member] = region.Start.Id;
            }
        }
        return owners;
    }
}
