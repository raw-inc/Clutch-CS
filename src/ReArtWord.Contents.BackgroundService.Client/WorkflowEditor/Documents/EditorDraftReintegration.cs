using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>統合した保存版へ、統合要求後の端末編集だけを重ねます。</summary>
internal static class EditorDraftReintegration
{
    /// <summary>確認したローカル基準版が同じ場合だけ共有基準へ進めます。</summary>
    /// <returns>後続編集を保持した草稿です。</returns>
    internal static EditorDraft Apply(EditorDraft draft, StoredWorkflow shared, string localRevision)
    {
        if (!draft.LocalOnly || !string.Equals(draft.Document.Id, shared.Definition.Id, StringComparison.Ordinal)
            || !string.Equals(draft.BaseRevision, localRevision, StringComparison.Ordinal) || shared.LocalOnly)
        {
            return draft;
        }
        WorkflowMergeResult merged = EditorDraftResolution.Merge(draft.Base, shared.Definition, draft.Document);
        WorkflowDefinition document = merged.Candidate ?? draft.Document;
        return draft with
        {
            Document = document,
            Base = shared.Definition,
            BaseRevision = shared.Revision,
            Original = shared.Definition,
            DraftBaseRevision = shared.Revision,
            SessionVersion = null,
            Pending = null,
            LocalOnly = false,
            Conflicts = merged.Conflicts,
            Dirty = draft.InputDrafts?.Count is > 0 || !string.Equals(ContractJson.Hash(document), shared.Revision, StringComparison.Ordinal),
        };
    }
}
