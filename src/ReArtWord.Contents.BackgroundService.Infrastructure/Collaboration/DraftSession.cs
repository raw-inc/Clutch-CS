using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

// LEGACY-FORMAT: draft-announcement-v1 — 旧一括草稿の取込みは旧Clientのサポート終了後に撤去します。
/// <summary>一つの下書きの比較元、競合候補と有限の再送履歴です。</summary>
internal sealed class DraftSession
{
    private readonly Dictionary<Guid, string> operations = [];
    private readonly DraftLineage lineage = new();

    /// <summary>保存版または新規作成元から開始します。</summary>
    public DraftSession(WorkflowDefinition original, string? baseRevision, DateTimeOffset now)
    {
        Original = ContractJson.Copy(original);
        View = new DraftView(original.Id, baseRevision, ContractJson.Hash(original), ContractJson.Copy(original), [], [], now);
    }

    /// <summary>再接続比較に使う共通編集元です。</summary>
    public WorkflowDefinition Original
    {
        get; private set;
    }

    /// <summary>未保存の現在候補です。</summary>
    public DraftView View
    {
        get; private set;
    }

    /// <summary>参照中の未保存草稿は保持し、編集の版番号は増やしません。</summary>
    internal void Touch(DateTimeOffset now) => View = View with { UpdatedAt = now };

    /// <summary>新しい保存版を未保存範囲と三者比較し、元版を更新します。</summary>
    public void Rebase(StoredWorkflow saved, DateTimeOffset now)
    {
        if (string.Equals(View.BaseRevision, saved.Revision, StringComparison.Ordinal))
        {
            return;
        }
        WorkflowMergeResult merge = WorkflowEditorMerge.Merge(Original, saved.Definition, new WorkflowMergeRequest(Guid.NewGuid(), View.BaseRevision ?? string.Empty, View.Definition, new Dictionary<string, EEditResolution>(StringComparer.Ordinal)));
        if (merge.Candidate is not null)
        {
            // 保存版の確認では編集順を進めず、遅れて届く解消通知との前後関係を保ちます。
            lineage.Remember(View);
            Original = ContractJson.Copy(saved.Definition);
            View = View with { BaseRevision = saved.Revision, Definition = merge.Candidate, SessionVersion = ContractJson.Hash(merge.Candidate), Conflicts = View.Conflicts.Concat(merge.Conflicts).DistinctBy(ContractJson.Hash).ToArray(), UpdatedAt = now };
        }
    }

    /// <summary>内容の同一性を確認し、非競合編集と未解消候補を保管します。</summary>
    /// <returns>更新候補または要求の問題。</returns>
    public DraftView Apply(DraftChangeRequest request, DateTimeOffset now, int capacity, int maximumBytes)
    {
        string fingerprint = ContractJson.Hash(request);
        if (operations.TryGetValue(request.OperationId, out string? previous))
        {
            if (!string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                return Problem("draft.operationMismatch", "同じ操作IDの内容が異なります。");
            }
            return View;
        }
        string? claimedVersion = request.BaseSessionVersion ?? request.BaseRevision;
        if (claimedVersion is not null && !string.Equals(claimedVersion, ContractJson.Hash(request.Original), StringComparison.Ordinal))
        {
            return Problem("draft.baseMismatch", "編集元の内容と版識別子が一致しません。");
        }
        WorkflowMergeResult merged = WorkflowEditorMerge.Merge(request.Original, View.Definition, new WorkflowMergeRequest(request.OperationId, request.BaseRevision ?? string.Empty, request.Edited, request.Resolutions));
        if (merged.Candidate is null)
        {
            return View with { Validation = merged.Validation };
        }
        bool whole = request.Resolutions.TryGetValue(string.Empty, out EEditResolution choice) && choice != EEditResolution.Cancel;
        EditConflict[] conflicts = View.Conflicts.Where(item => !whole && (!request.Resolutions.TryGetValue(item.Path, out EEditResolution resolution) || resolution == EEditResolution.Cancel))
            .Concat(merged.Conflicts).DistinctBy(ContractJson.Hash).ToArray();
        if (capacity < conflicts.Length)
        {
            return Problem("draft.conflictCapacity", "未解消候補が上限に達しました。候補を解消してください。");
        }
        DraftView candidate = new(View.WorkflowId, View.BaseRevision, ContractJson.Hash(merged.Candidate), merged.Candidate, conflicts, merged.Validation, now);
        if (maximumBytes < JsonSerializer.SerializeToUtf8Bytes(candidate, ContractJson.Options).Length)
        {
            return Problem("draft.capacity", "統合後の下書きが保持上限を超えます。");
        }
        lineage.Advance(View);
        View = candidate;
        operations[request.OperationId] = fingerprint;
        _ = Announce(capacity, maximumBytes);
        while (capacity < operations.Count)
        {
            operations.Remove(operations.Keys.First());
        }
        return View;
    }

    /// <summary>本文・解消結果とその前後関係を同じ通知で渡します。</summary>
    /// <returns>版番号と再送識別を添えた状態です。</returns>
    internal DraftAnnouncement Announce(int capacity, int maximumBytes)
        => lineage.Announce(new(Original, View, Operations: new Dictionary<Guid, string>(operations)), capacity, maximumBytes);

    /// <summary>古い複製を再編集として扱わず、実際の並行変更だけを比較します。</summary>
    public void MergeAnnouncement(DraftAnnouncement announcement, DateTimeOffset now, int capacity, int maximumBytes)
    {
        if (announcement.Clock is null)
        {
            MergeLegacy(announcement, now, capacity, maximumBytes);
            return;
        }
        if (lineage.Contains(announcement.Clock))
        {
            return;
        }
        DraftView before = View;
        if (lineage.IsContainedBy(announcement.Clock))
        {
            lineage.Remember(before);
            Original = ContractJson.Copy(announcement.Original);
            View = ContractJson.Copy(announcement.Draft) with { UpdatedAt = now };
        }
        else
        {
            DraftView? common = lineage.Common(announcement);
            WorkflowMergeResult merged;
            if (common is null && !string.Equals(View.SessionVersion, announcement.Draft.SessionVersion, StringComparison.Ordinal))
            {
                // 共通時点を保持していない並行編集を、保存版からの独立変更と推測しません。
                EditConflict conflict = new(string.Empty, null, JsonSerializer.SerializeToElement(View.Definition, ContractJson.Options),
                    JsonSerializer.SerializeToElement(announcement.Draft.Definition, ContractJson.Options), false, true, true);
                merged = new(View.Definition, [conflict], [], null);
            }
            else
            {
                merged = WorkflowEditorMerge.Merge(common?.Definition ?? View.Definition, View.Definition,
                    new(Guid.NewGuid(), View.BaseRevision ?? string.Empty, announcement.Draft.Definition, new Dictionary<string, EEditResolution>()));
            }
            if (merged.Candidate is null)
            {
                return;
            }
            HashSet<string> shared = (common?.Conflicts ?? []).Select(ContractJson.Hash).ToHashSet(StringComparer.Ordinal);
            HashSet<string> local = View.Conflicts.Select(ContractJson.Hash).ToHashSet(StringComparer.Ordinal);
            HashSet<string> remote = announcement.Draft.Conflicts.Select(ContractJson.Hash).ToHashSet(StringComparer.Ordinal);
            EditConflict[] conflicts = View.Conflicts.Concat(announcement.Draft.Conflicts)
                .Where(item => !shared.Contains(ContractJson.Hash(item)) || local.Contains(ContractJson.Hash(item)) && remote.Contains(ContractJson.Hash(item)))
                .Concat(merged.Conflicts).DistinctBy(ContractJson.Hash).ToArray();
            if (capacity < conflicts.Length)
            {
                return;
            }
            if (!string.Equals(View.SessionVersion, ContractJson.Hash(merged.Candidate), StringComparison.Ordinal)
                || !View.Conflicts.Select(ContractJson.Hash).ToHashSet(StringComparer.Ordinal).SetEquals(conflicts.Select(ContractJson.Hash)))
            {
                lineage.Advance(before);
            }
            else
            {
                lineage.Remember(before);
            }
            View = View with { Definition = merged.Candidate, SessionVersion = ContractJson.Hash(merged.Candidate), Conflicts = conflicts, UpdatedAt = now };
        }
        lineage.Join(announcement);
        foreach ((Guid id, string fingerprint) in announcement.Operations ?? new Dictionary<Guid, string>())
        {
            operations.TryAdd(id, fingerprint);
        }
        while (capacity < operations.Count)
        {
            operations.Remove(operations.Keys.First());
        }
        _ = Announce(capacity, maximumBytes);
    }

    /// <summary>更新途中の旧Peerの通知だけは従来の比較で保持します。</summary>
    private void MergeLegacy(DraftAnnouncement announcement, DateTimeOffset now, int capacity, int maximumBytes)
    {
        DraftChangeRequest request = new(Guid.NewGuid(), View.WorkflowId, Guid.Empty, announcement.Draft.BaseRevision, null, announcement.Original, announcement.Draft.Definition, new Dictionary<string, EEditResolution>(StringComparer.Ordinal));
        Apply(request, now, capacity, maximumBytes);
        EditConflict[] conflicts = View.Conflicts.Concat(announcement.Draft.Conflicts).DistinctBy(ContractJson.Hash).ToArray();
        if (conflicts.Length <= capacity && JsonSerializer.SerializeToUtf8Bytes(View with { Conflicts = conflicts }, ContractJson.Options).Length <= maximumBytes)
        {
            View = View with { Conflicts = conflicts };
        }
    }

    /// <summary>保存状態を変えず要求の問題を返します。</summary>
    /// <returns>候補と問題。</returns>
    private DraftView Problem(string code, string message) => View with { Validation = [new ValidationIssue(code, message)] };
}
