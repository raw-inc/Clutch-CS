using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>接続から反復範囲を確定し、交差と完了待ちの循環を開始前に拒否します。</summary>
internal static class IterationCompiler
{
    /// <summary>End省略を許可し、存在するEndは専用loop接続で一意に対応させます。</summary>
    /// <returns>反復範囲を持つ計画または具体的な接続エラーです。</returns>
    internal static CompilationResult Bind(CompiledWorkflow workflow, NodeRegistry registry, IReadOnlyDictionary<string, IReadOnlyList<string>>? recipeInputs = null)
    {
        Contracts.Workflow.Regions.WorkflowRegionMap map = Contracts.Workflow.Regions.WorkflowRegionMap.Create(workflow.Definition, recipeInputs ?? workflow.RecipeInputs);
        List<ValidationIssue> issues = [.. map.Issues];
        Dictionary<string, NodeDefinition> nodes = workflow.OrderedNodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        List<IterationRegion> regions = map.Regions.Select(region =>
        {
            NodeDefinition? end = null;
            if (region.End is not null)
            {
                end = nodes[region.End.Id];
            }
            return new IterationRegion(nodes[region.Start.Id], end, region.Members.ToHashSet(StringComparer.Ordinal));
        }).ToList();
        foreach (IterationRegion region in regions)
        {
            foreach (string id in region.Members.Where(id => registry.Find(nodes[id].Type)!.Descriptor.Kind == ENodeKind.Source))
            {
                issues.Add(new("iteration.source", "A finite iteration body cannot contain a continuous source.", id));
            }
            if (workflow.Definition.Wires.Any(wire => string.Equals(wire.FromNode, region.Start.Id, StringComparison.Ordinal) && region.Members.Contains(wire.ToNode) && !WorkflowRegionNodeTypes.IsBodyPort(region.Start.Type, wire.FromPort) && !string.Equals(wire.FromPort, WorkflowRegionNodeTypes.PairPort(region.Start.Type), StringComparison.Ordinal)))
            {
                issues.Add(new("iteration.completion", "The start result is available after the body completes; it cannot feed the same body.", region.Start.Id));
            }
        }
        foreach (IterationRegion region in regions)
        {
            if (16 <= regions.Count(parent => region.Members.IsProperSubsetOf(parent.Members)))
            {
                issues.Add(new("iteration.depth", "Iteration nesting is limited to 16 levels.", region.Start.Id));
            }
            if (region.End is not null && regions.Any(inner => inner.Members.IsProperSubsetOf(region.Members) && inner.Members.Contains(region.End.Id)))
            {
                issues.Add(new("iteration.endScope", "An inner iteration cannot collect directly into an outer End. Add an inner End before collecting its results in the outer loop.", region.End.Id));
            }
            if (regions.Any(other => !ReferenceEquals(region, other) && region.Members.Overlaps(other.Members) && !region.Members.IsProperSubsetOf(other.Members) && !other.Members.IsProperSubsetOf(region.Members)))
            {
                issues.Add(new("iteration.crossing", "Iteration bodies must be separate or fully nested; they cannot overlap partially.", region.Start.Id));
            }
        }
        workflow = workflow with
        {
            RecipeInputs = map.Inherited,
            ScopedValueMerges = map.ScopedValueMerges,
        };
        IterationPlan plan = new(regions);
        if (issues.Count == 0 && regions.Count != 0)
        {
            ValidateScope(workflow, plan, nodes.Keys.ToHashSet(StringComparer.Ordinal), null, issues);
            foreach (IterationRegion region in regions)
            {
                ValidateScope(workflow, plan, region.Members, region.Start.Id, issues);
            }
        }
        if (issues.Count != 0)
        {
            return new(null, issues);
        }
        return new(workflow with { Iterations = plan }, []);
    }

    /// <summary>ボディ外の値を待つ依存が、自分の完了へ循環しないことを確認します。</summary>
    private static void ValidateScope(CompiledWorkflow workflow, IterationPlan plan, HashSet<string> members, string? activeStart, List<ValidationIssue> issues)
    {
        Dictionary<string, string> owners = plan.Owners(members, activeStart);
        Dictionary<string, HashSet<string>> dependencies = owners.Values.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach ((string node, string parent) in workflow.OrderedNodes.SelectMany(node => workflow.Parents(node.Id).Select(parent => (node.Id, parent))))
        {
            if (owners.TryGetValue(node, out string? target) && owners.TryGetValue(parent, out string? source) && !string.Equals(target, source, StringComparison.Ordinal))
            {
                dependencies[target].Add(source);
            }
        }
        while (dependencies.Count != 0)
        {
            string[] ready = dependencies.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray();
            if (ready.Length == 0)
            {
                issues.Add(new("iteration.dependencyCycle", "An iteration body depends on its own completion. Move the shared dependency before the loop.", activeStart));
                return;
            }
            foreach (string id in ready)
            {
                dependencies.Remove(id);
                foreach (HashSet<string> parents in dependencies.Values)
                {
                    parents.Remove(id);
                }
            }
        }
    }
}
