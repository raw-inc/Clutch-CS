using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>共有方針を確認したCoordinatorだけが、落ち着いた草稿を通常の保存経路へ渡します。</summary>
public sealed class DraftAutoApply(WorkflowStore workflows, CollaborationService collaboration, PeerCluster cluster,
    IClusterOperationalProvider operational, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkflowSaveResult> results = new(StringComparer.Ordinal);

    /// <summary>このCoordinatorで最後に確認した自動反映結果です。</summary>
    /// <returns>未開始ならnull、開始後は各段階を分けた結果です。</returns>
    public async Task<WorkflowSaveResult?> ReadResultAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (!results.TryGetValue(workflowId, out WorkflowSaveResult? result))
        {
            return null;
        }
        if (result.Saved.Outcome == EOutcome.Unknown || result.Shared.Outcome == EOutcome.Unknown || result.Snapshot.Outcome == EOutcome.Unknown || result.Activation.Outcome == EOutcome.Unknown)
        {
            result = await workflows.GetSaveOperationAsync(result.OperationId, cancellationToken).ConfigureAwait(false) ?? result;
            results[workflowId] = result;
        }
        return ContractJson.Copy(result);
    }

    /// <summary>型検証・CAS・スナップショット・変更種類別反映方針を省略しない一巡です。</summary>
    /// <returns>現Coordinatorで開始可能な保存の確認です。</returns>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (cluster.IsDetached || !string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal)
            || operational.Capture()?.Settings.LiveWorkflowUpdates != true
            || !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && peer.Status == EPeerStatus.Ready))
        {
            candidates.Clear();
            return;
        }
        IReadOnlyList<DraftView> drafts = collaboration.CaptureDrafts(changedOnly: true);
        foreach (string id in candidates.Keys.Except(drafts.Select(draft => draft.WorkflowId), StringComparer.Ordinal).ToArray())
        {
            candidates.TryRemove(id, out _);

        }
        foreach (DraftView draft in drafts)
        {
            if (draft.Conflicts.Count != 0 || draft.Validation.Count != 0 || draft.Definition.Nodes.Count == 0 && draft.BaseRevision is null)
            {
                candidates.TryRemove(draft.WorkflowId, out _);
                continue;
            }
            StoredWorkflow? saved = await workflows.GetAsync(draft.WorkflowId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(saved?.Revision, draft.SessionVersion, StringComparison.Ordinal)
                || saved is null && draft.BaseRevision is not null)
            {
                candidates.TryRemove(draft.WorkflowId, out _);
                continue;
            }
            if (!string.Equals(saved?.Revision, draft.BaseRevision, StringComparison.Ordinal))
            {
                await collaboration.GetDraftAsync(draft.WorkflowId, cancellationToken).ConfigureAwait(false);
                candidates.TryRemove(draft.WorkflowId, out _);
                continue;
            }
            string fingerprint = ContractJson.Hash(new { draft.BaseRevision, draft.SessionVersion });
            if (!candidates.TryGetValue(draft.WorkflowId, out Candidate? candidate) || !string.Equals(candidate.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                candidates[draft.WorkflowId] = new(fingerprint, clock.GetUtcNow());
                continue;
            }
            if (clock.GetUtcNow() - candidate.StableSince < TimeSpan.FromSeconds(2) || candidate.Attempted)
            {
                continue;
            }
            if (operational.Capture()?.Settings.LiveWorkflowUpdates != true || !string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal))
            {
                return;
            }
            DraftView? current = collaboration.CaptureDraft(draft.WorkflowId);
            if (current is null || current.Conflicts.Count != 0 || !string.Equals(current.SessionVersion, draft.SessionVersion, StringComparison.Ordinal))
            {
                candidates.TryRemove(draft.WorkflowId, out _);
                continue;
            }
            Guid operation = WorkflowStore.PhaseId(Guid.Empty, "editor.live:" + draft.WorkflowId + ":" + fingerprint);
            candidates[draft.WorkflowId] = candidate with { Attempted = true };
            WorkflowSaveResult? known = await workflows.GetSaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            if (known is null)
            {
                known = await workflows.SaveAsync(new(operation, draft.Definition, saved?.Revision, EActivationPolicy.Auto), cancellationToken).ConfigureAwait(false);
            }
            results[draft.WorkflowId] = known;
            while (256 < results.Count)
            {
                results.TryRemove(results.Keys.First(), out _);
            }
            if (known.Saved.Outcome == EOutcome.Succeeded && known.Shared.Outcome == EOutcome.Succeeded)
            {
                await collaboration.GetDraftAsync(draft.WorkflowId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>同じ候補への暗黙再送を防ぎ、操作していない時間だけを待機に数えます。</summary>
    private sealed record Candidate(string Fingerprint, DateTimeOffset StableSince, bool Attempted = false);
}
