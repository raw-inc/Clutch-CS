using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>実行計画の配置を優先し、未反映の保存済み配置も実行せずに表示します。</summary>
internal static class WorkflowRecipeInspection
{
    /// <summary>不変のレイアウトは繰返し転送せず、型も同じ展開版へ揃えます。</summary>
    /// <returns>変更した表示内容、または同一版ならnullです。</returns>
    internal static async Task<WorkflowRecipePreview?> ReadAsync(ServiceRuntime service, WorkflowRecipePreviewQuery query, CancellationToken cancellationToken = default)
    {
        CompiledWorkflow? plan = service.Sources.GetEffective(query.WorkflowId);
        IReadOnlyList<WorkflowRecipeInstance> instances = plan?.RecipeInstances ?? [];
        WorkflowRecipeInstance? instance = instances.FirstOrDefault(item => string.Equals(item.NodeId, query.NodeId, StringComparison.Ordinal));
        bool runtime = instance is not null;
        string? revision = plan?.Revision;
        if (instance is null)
        {
            StoredWorkflow saved = await service.Workflows.GetAsync(query.WorkflowId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
            instances = service.Compiler.InspectRecipes(saved.Definition);
            instance = instances.FirstOrDefault(item => string.Equals(item.NodeId, query.NodeId, StringComparison.Ordinal)) ?? throw new KeyNotFoundException();
            revision = saved.Revision;
        }
        IReadOnlyList<Contracts.Configuration.ValidationIssue> issues = plan?.DependencyIssues ?? [];
        string hash = ContractJson.Hash(new { instance.Layout, revision, issues, runtime });
        if (string.Equals(hash, query.KnownHash, StringComparison.Ordinal))
        {
            return null;
        }
        Dictionary<string, NodeDescriptor> catalog = service.Registry.Descriptors.ToDictionary(item => item.Type, StringComparer.Ordinal);
        foreach (NodeDefinition node in instance.Layout.Nodes)
        {
            WorkflowRecipeInstance? nested = instances.FirstOrDefault(item => string.Equals(item.NodeId, node.Id, StringComparison.Ordinal));
            if (nested is not null)
            {
                catalog[node.Type] = SubworkflowRecipeDescriptor.Create(nested.Layout with { Id = nested.DefinitionId });
            }
        }
        return new(query.WorkflowId, revision!, instance, issues, hash,
            instance.Layout.Nodes.Select(node => catalog.GetValueOrDefault(node.Type) ?? service.Registry.Find(node.Type)?.Descriptor).OfType<NodeDescriptor>().DistinctBy(item => item.Type).ToArray(), runtime);
    }
}
