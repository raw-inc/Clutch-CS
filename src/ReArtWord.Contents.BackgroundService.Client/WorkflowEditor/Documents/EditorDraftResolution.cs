using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>人が破棄した範囲だけをローカル草稿へ反映します。</summary>
internal static class EditorDraftResolution
{
    /// <summary>種類を変換せず、明示した取込文書を新しい比較元として扱います。</summary>
    /// <returns>保存先の版を後から確認する取込草稿です。</returns>
    internal static EditorDraft Imported(WorkflowDefinition current, WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        CheckKind(current, definition);
        return new(definition, definition, null, definition, null, null, true, [], NeedsBaseline: true);
    }

    /// <summary>同じ文書IDの種類を編集や取込で変更しません。</summary>
    internal static void CheckKind(WorkflowDefinition current, WorkflowDefinition next)
    {
        if (string.Equals(current.Id, next.Id, StringComparison.Ordinal) && current.IsDashboard != next.IsDashboard)
        {
            throw Localization.UiError.InvalidOperation("WFDashboard and Workflow cannot be converted into each other.");
        }
    }

    /// <summary>未検証入力を草稿へ保持し、確定後だけ削除します。</summary>
    /// <returns>入力値の所有権を分離した草稿です。</returns>
    internal static EditorDraft WithInput(EditorDraft draft, string key, string? raw)
    {
        if (raw is null && draft.InputDrafts?.ContainsKey(key) != true)
        {
            return draft;
        }
        if (raw is not null && string.Equals(draft.InputDrafts?.GetValueOrDefault(key), raw, StringComparison.Ordinal))
        {
            return draft;
        }
        Dictionary<string, string> values = draft.InputDrafts?.ToDictionary(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        if (raw is null)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = raw;
        }
        return draft with { InputDrafts = values, Dirty = true };
    }

    /// <summary>確認済み保存を比較元へ反映し、強制保存の送信後に加わった編集は残します。</summary>
    /// <returns>通常保存・ローカル保存・明示上書きの草稿状態です。</returns>
    internal static EditorDraft Saved(EditorDraft draft, string revision, WorkflowDefinition definition, bool localOnly, bool force)
    {
        if (force)
        {
            Dictionary<string, EEditResolution> resolutions = draft.Conflicts.Select(item => item.Path).Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, _ => EEditResolution.UseEdited, StringComparer.Ordinal);
            DraftChangeRequest reconcile = new(Guid.NewGuid(), definition.Id, Guid.NewGuid(), draft.DraftBaseRevision,
                WorkflowDraftOrder.Revision(draft.Original), draft.Original, definition, resolutions);
            return draft with { Base = definition, BaseRevision = revision, Pending = reconcile, Conflicts = [], SharedConflicts = [],
                SessionVersion = WorkflowDraftOrder.Revision(draft.Original), NeedsBaseline = false, LocalOnly = false,
                Dirty = !WorkflowDraftOrder.Same(draft.Document, definition) };
        }
        EditorDraft saved = draft with { Document = definition, Base = definition, BaseRevision = revision, Dirty = false, LocalOnly = localOnly };
        if (localOnly)
        {
            saved = saved with { Original = definition, DraftBaseRevision = revision, SessionVersion = null, Pending = null, NeedsBaseline = false };
        }
        return saved;
    }

    /// <summary>共有草稿と保存版が一致した場合は、別ブラウザーや自動保存の成功も認識します。</summary>
    /// <returns>送信中のローカル差分を保持した編集状態です。</returns>
    internal static EditorDraft Accepted(EditorDraft draft, DraftView view, WorkflowDefinition candidate, IReadOnlyList<EditConflict> conflicts)
    {
        WorkflowDefinition basis = draft.Base;
        string? revision = draft.BaseRevision;
        if (!draft.LocalOnly && view.BaseRevision is not null && string.Equals(view.BaseRevision, view.SessionVersion, StringComparison.Ordinal))
        {
            basis = view.Definition;
            revision = view.BaseRevision;
        }
        return draft with { Document = candidate, Base = basis, BaseRevision = revision, Original = view.Definition,
            SessionVersion = view.SessionVersion, DraftBaseRevision = view.BaseRevision, Pending = null, Conflicts = conflicts,
            SharedConflicts = view.Conflicts, Dirty = (draft.InputDrafts?.Count ?? 0) != 0 || !WorkflowDraftOrder.Same(candidate, basis) };
    }

    /// <summary>共有側で解消した候補だけを置き換え、未送信のローカル競合を保持します。</summary>
    /// <returns>現在の未解消候補です。</returns>
    internal static EditConflict[] Conflicts(EditorDraft draft, DraftView view, WorkflowMergeResult merged)
    {
        IReadOnlyDictionary<string, EEditResolution> resolutions = draft.Pending?.Resolutions ?? new Dictionary<string, EEditResolution>();
        HashSet<string> shared = (draft.SharedConflicts ?? []).Select(ContractJson.Hash).ToHashSet(StringComparer.Ordinal);
        return draft.Conflicts.Where(conflict => !resolutions.ContainsKey(string.Empty) && !resolutions.ContainsKey(conflict.Path) && !shared.Contains(ContractJson.Hash(conflict)))
            .Concat(view.Conflicts).Concat(merged.Conflicts).DistinctBy(ContractJson.Hash).ToArray();
    }

    /// <summary>送信中の追加編集をサーバーと同じ比較実装へ渡します。</summary>
    /// <returns>非競合候補と競合です。</returns>
    public static WorkflowMergeResult Merge(WorkflowDefinition original, WorkflowDefinition current, WorkflowDefinition edited) => WorkflowEditorMerge.Merge(original, current, new WorkflowMergeRequest(Guid.NewGuid(), string.Empty, edited, new Dictionary<string, EEditResolution>()));

    /// <summary>範囲外の手元入力を保ち、破棄したノードと接続を共有候補へ戻します。</summary>
    /// <returns>新しい共有版と未確定入力を持つ草稿です。</returns>
    internal static EditorDraft Apply(EditorDraft draft, DraftView view, StoredWorkflow saved, IReadOnlyList<string>? nodeIds)
    {
        WorkflowMergeResult merge = EditorDraftResolution.Merge(draft.Original, view.Definition, draft.Document);
        WorkflowDefinition document = view.Definition;
        Dictionary<string, string> raw = [];
        IReadOnlyList<EditConflict> conflicts = [];
        if (nodeIds is not null)
        {
            document = WorkflowSnapshotSelection.Patch(merge.Candidate ?? draft.Document, view.Definition, nodeIds);
            raw = (draft.InputDrafts ?? new Dictionary<string, string>()).Where(pair => !nodeIds.Any(id => pair.Key.StartsWith(id + "/settings/", StringComparison.Ordinal) || pair.Key.StartsWith(id + "/inputs/", StringComparison.Ordinal))).ToDictionary(StringComparer.Ordinal);
            conflicts = draft.Conflicts.Concat(merge.Conflicts).Concat(view.Conflicts).Where(conflict => !Touches(conflict.Path, nodeIds)).DistinctBy(ContractJson.Hash).ToArray();
        }
        bool dirty = raw.Count != 0 || !string.Equals(ContractJson.Hash(document), ContractJson.Hash(saved.Definition), StringComparison.Ordinal);
        return new(document, saved.Definition, saved.Revision, view.Definition, view.SessionVersion, view.BaseRevision, dirty, conflicts, InputDrafts: raw, SharedConflicts: view.Conflicts);
    }

    /// <summary>選択範囲に属する競合だけを明示破棄します。</summary>
    /// <returns>競合箇所が対象内か。</returns>
    private static bool Touches(string path, IReadOnlyList<string> nodes)
    {
        string[] parts = path.TrimStart('/').Split('/').Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        if (parts.Length < 2)
        {
            return false;
        }
        string nodeKey = "nodes";
        string wireKey = "wires";
        if (string.Equals(parts[0], nodeKey, StringComparison.Ordinal))
        {
            return nodes.Contains(parts[1], StringComparer.Ordinal);
        }
        if (string.Equals(parts[0], wireKey, StringComparison.Ordinal))
        {
            string[]? endpoints = JsonSerializer.Deserialize<string[]>(parts[1]);
            return endpoints?.Length == 4 && (nodes.Contains(endpoints[0], StringComparer.Ordinal) || nodes.Contains(endpoints[2], StringComparer.Ordinal));
        }
        return false;
    }
}
