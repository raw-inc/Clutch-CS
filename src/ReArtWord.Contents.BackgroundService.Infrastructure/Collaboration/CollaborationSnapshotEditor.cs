using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>復元前の下書き調停を確定保存から分離します。</summary>
internal sealed class CollaborationSnapshotEditor(CollaborationService collaboration, WorkflowStore workflows, PeerCluster cluster)
{
    /// <summary>現在保存版と選択範囲を比較し、他UIの未保存フォームも保持します。</summary>
    /// <returns>復元開始を妨げる未確認編集です。</returns>
    public async Task<SnapshotEditorState> InspectAsync(SnapshotEditLock scope, CancellationToken cancellationToken = default)
    {
        DraftView? draft = await collaboration.GetDraftAsync(scope.WorkflowId, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? saved = await workflows.GetAsync(scope.WorkflowId, cancellationToken).ConfigureAwait(false);
        PresenceView[] presence = collaboration.GetPresence(scope.WorkflowId).Where(item => string.Equals(item.PeerId, cluster.LocalPeerId, StringComparison.Ordinal) && item.PeerInstanceId == cluster.LocalInstanceId && Affects(item.Presence, scope)).ToArray();
        bool dirty = draft is not null && (!string.Equals(SnapshotNodeSelection.Hash(draft.Definition, scope.NodeIds), SnapshotNodeSelection.Hash(saved?.Definition, scope.NodeIds), StringComparison.Ordinal) || draft.Conflicts.Any(item => SnapshotNodeSelection.ContainsConflict(item.Path, scope.NodeIds)));
        return new SnapshotEditorState(cluster.LocalPeerId, scope.WorkflowId, scope.NodeIds, draft?.SessionVersion, dirty, presence);
    }

    /// <summary>別UIの入力を承認せず、確認した元版の選択範囲だけを調停します。</summary>
    /// <returns>未保存編集の調停結果です。</returns>
    public async Task<ConfigurationPhaseResult> ResolveAsync(SnapshotDraftResolution request, SnapshotEditLock scope, CancellationToken cancellationToken = default)
    {
        if (request.Action == ESnapshotDraftAction.CancelRestore)
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.RestoreCancelled");
        }
        DraftView? draft = await collaboration.GetDraftAsync(scope.WorkflowId, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? saved = await workflows.GetAsync(scope.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (draft is null || saved is null || request.UiInstanceId == Guid.Empty || !string.Equals(draft.SessionVersion, request.ExpectedSessionVersion, StringComparison.Ordinal))
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.DraftChanged");
        }
        PresenceView? own = collaboration.GetPresence(scope.WorkflowId).FirstOrDefault(item => string.Equals(item.PeerId, cluster.LocalPeerId, StringComparison.Ordinal) && item.PeerInstanceId == cluster.LocalInstanceId && item.Presence.UiInstanceId == request.UiInstanceId);
        if (scope.NodeIds is not null && own?.Presence is { Dirty: true, DirtyNodeIds: null })
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.EditorDirtyScopeUnknown");
        }
        if (request.Action == ESnapshotDraftAction.ConfirmSaved)
        {
            if (!string.Equals(SnapshotNodeSelection.Hash(draft.Definition, scope.NodeIds), SnapshotNodeSelection.Hash(saved.Definition, scope.NodeIds), StringComparison.Ordinal) || draft.Conflicts.Any(item => SnapshotNodeSelection.ContainsConflict(item.Path, scope.NodeIds)))
            {
                return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.DraftNotSaved");
            }
        }
        else
        {
            WorkflowDefinition desired = saved.Definition;
            if (scope.NodeIds is not null)
            {
                desired = WorkflowSnapshotSelection.Patch(draft.Definition, saved.Definition, scope.NodeIds);
            }
            Dictionary<string, EEditResolution> resolutions = draft.Conflicts.Where(item => SnapshotNodeSelection.ContainsConflict(item.Path, scope.NodeIds)).Select(item => item.Path).Distinct(StringComparer.Ordinal).ToDictionary(path => path, _ => EEditResolution.UseEdited, StringComparer.Ordinal);
            DraftChangeRequest change = new(Guid.NewGuid(), scope.WorkflowId, request.UiInstanceId, draft.BaseRevision, draft.SessionVersion, draft.Definition, desired, resolutions);
            DraftView result = await collaboration.ApplyChangesAsync(change, cancellationToken).ConfigureAwait(false);
            if (result.Validation.Count != 0 || !string.Equals(SnapshotNodeSelection.Hash(result.Definition, scope.NodeIds), SnapshotNodeSelection.Hash(desired, scope.NodeIds), StringComparison.Ordinal))
            {
                return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.DraftChanged");
            }
        }
        if (own is not null)
        {
            PresenceUpdate confirmed = own.Presence with { Dirty = false, DirtyNodeIds = [] };
            if (scope.NodeIds is not null && own.Presence.DirtyNodeIds is not null)
            {
                string[] remaining = own.Presence.DirtyNodeIds.Except(scope.NodeIds, StringComparer.Ordinal).ToArray();
                confirmed = own.Presence with { DirtyNodeIds = remaining };
            }
            await collaboration.UpdatePresenceAsync(confirmed, cancellationToken).ConfigureAwait(false);
        }
        return new ConfigurationPhaseResult(EOutcome.Succeeded);
    }

    /// <summary>選択が空の未保存フォームは範囲不明として確認を要求します。</summary>
    /// <returns>編集中の表示が復元対象に接するか。</returns>
    private static bool Affects(PresenceUpdate presence, SnapshotEditLock scope) => scope.NodeIds is null || presence.DirtyNodeIds is null || presence.DirtyNodeIds.Intersect(scope.NodeIds, StringComparer.Ordinal).Any();
}
