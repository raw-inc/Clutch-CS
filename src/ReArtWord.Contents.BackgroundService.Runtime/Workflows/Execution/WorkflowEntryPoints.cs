using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>手動開始と子ワークフロー開始で同じ有限ノードの入口を選びます。</summary>
internal static class WorkflowEntryPoints
{
    /// <summary>明示入口を検証し、未指定なら入力依存のない有限処理を選びます。</summary>
    /// <returns>一件で開始する入口の集合です。</returns>
    internal static HashSet<string> Select(CompiledWorkflow workflow, NodeRegistry registry, string? entryNodeId)
    {
        HashSet<string> entries = new(StringComparer.Ordinal);
        if (entryNodeId is not null)
        {
            if (workflow.RecipeInstances.Any(instance => string.Equals(instance.NodeId, entryNodeId, StringComparison.Ordinal)))
            {
                entries.UnionWith(workflow.OrderedNodes.Where(node => RecipeNodeIdentity.Contains(entryNodeId, node.Id)
                    && registry.Find(node.Type)?.Descriptor.Kind == ENodeKind.Action && node.Type is not ("control.button" or "control.toggleButton")
                    && workflow.Incoming[node.Id].All(wire => !RecipeNodeIdentity.Contains(entryNodeId, wire.FromNode))).Select(node => node.Id));
                return entries;
            }
            if (!workflow.Incoming.ContainsKey(entryNodeId))
            {
                throw new ArgumentException("指定した入口ノードがありません。", nameof(entryNodeId));
            }
            entries.Add(entryNodeId);
        }
        else
        {
            entries.UnionWith(workflow.OrderedNodes.Where(node => workflow.Incoming[node.Id].Count == 0 && registry.Find(node.Type)?.Descriptor.Kind == ENodeKind.Action && node.Type is not ("control.button" or "control.toggleButton")).Select(node => node.Id));
        }
        return entries;
    }
}
