using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>実共通祖先から非競合と確認できる設定だけを自動統合します。</summary>
internal static class ConflictAutoMerge
{
    /// <summary>不明・競合は元候補を保持し、同値または独立した編集だけを統合します。</summary>
    /// <returns>自動採用可能かと候補値です。</returns>
    internal static async Task<(bool Safe, NodeValue? Value)> CreateAsync(IReadOnlyList<DurableEntry> candidates, IDurableRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            if (candidates.Select(ConflictCandidateValidation.Meaning).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                return (true, candidates[0].Value?.Copy());
            }
            DurableEntry? original = await ConflictAncestry.FindAsync(candidates, repository, cancellationToken).ConfigureAwait(false);
            if (original?.Value is null || candidates.Any(candidate => candidate.Value is null))
            {
                return (false, null);
            }
            string scope = original.Scope;
            bool definition = scope is "workflows" or "workflow.activation";
            if (definition)
            {
                StoredWorkflow baseline = original.Value.Json.Deserialize<StoredWorkflow>()!;
                StoredWorkflow[] versions = candidates.Select(candidate => candidate.Value!.Json.Deserialize<StoredWorkflow>()!).ToArray();
                if (versions.Select(version => version.ActivationPolicy).Distinct().Count() != 1)
                {
                    return (false, null);
                }
                WorkflowDefinition merged = versions[0].Definition;
                foreach (StoredWorkflow next in versions.Skip(1))
                {
                    WorkflowMergeResult result = WorkflowEditorMerge.Merge(baseline.Definition, merged,
                        new(Guid.Empty, baseline.Revision, next.Definition, new Dictionary<string, EEditResolution>(StringComparer.Ordinal)));
                    if (result.Candidate is null || result.Conflicts.Count != 0 || result.Validation.Count != 0)
                    {
                        return (false, null);
                    }
                    merged = result.Candidate;
                }
                return (true, NodeValue.FromObject(new StoredWorkflow(merged, ContractJson.Hash(merged), versions.Max(version => version.SavedAt), versions[0].ActivationPolicy)));
            }
            if (ConflictCandidateValidation.IsOperational(scope))
            {
                return ConflictOperationalMerge.Create(original, candidates);
            }
            return (false, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return (false, null);
        }
    }
}
