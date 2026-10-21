using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>現在の実行計画で展開されている一つの配置を返します。</summary>
public sealed record WorkflowRecipePreview(string WorkflowId, string Revision, WorkflowRecipeInstance Instance,
    IReadOnlyList<Configuration.ValidationIssue>? Issues = null, string? Hash = null, IReadOnlyList<Nodes.NodeDescriptor>? Catalog = null, bool IsRuntime = true);
