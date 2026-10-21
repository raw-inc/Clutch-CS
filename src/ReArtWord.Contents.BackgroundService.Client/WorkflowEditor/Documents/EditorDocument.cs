using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>入力中の定義、選択、履歴とオフライン草稿を所有します。</summary>
internal sealed class EditorDocument(BrowserStorage storage)
{
    private Guid owner = Guid.NewGuid();
    private readonly List<WorkflowDefinition> history = [];
    private Task? initialization;

    /// <summary>復元中の編集範囲の保護です。</summary>
    internal EditorLocks Locks
    {
        get;
    } = new();

    /// <summary>現在の比較元と編集内容です。</summary>
    public EditorDraft Draft
    {
        get; private set;
    } = EditorDraftNavigation.Empty();

    /// <summary>明示的に開いているWFがある場合だけ編集・照会できます。</summary>
    internal bool HasDocument => !string.IsNullOrEmpty(Draft.Document.Id);

    /// <summary>画面内の選択ノードです。</summary>
    public string? SelectedNode
    {
        get => Selection.Primary;
        set => Selection.Single(value);
    }

    /// <summary>単一選択の詳細欄と複数選択操作の共有状態です。</summary>
    internal GraphSelection Selection
    {
        get;
    } = new();

    /// <summary>関連草稿を別画面の所有範囲へ混ぜない識別子です。</summary>
    internal Guid StorageOwner => owner;

    /// <summary>画面内の選択配線です。</summary>
    public int? SelectedWire
    {
        get; set;
    }

    /// <summary>表示対象の切替世代です。</summary>
    public int Generation
    {
        get; private set;
    }

    /// <summary>端末保存の問題です。</summary>
    public string? StorageProblem
    {
        get; private set;
    }

    /// <summary>型をまだ確認できていない入力文字列を別ノードへ移動しても保持します。</summary>
    public bool HasInvalidInputs => Draft.InputDrafts?.Count != 0 && Draft.InputDrafts is not null;

    /// <summary>未確定の入力文字列を取得します。</summary>
    /// <returns>保存している未加工の入力です。</returns>
    public string? ReadInput(string key) => Draft.InputDrafts?.GetValueOrDefault(key);

    /// <summary>型検証済み入力は解除し、未確定だけを端末保存対象に残します。</summary>
    public void SetInput(string key, string? raw)
    {
        EditorDraft next = EditorDraftResolution.WithInput(Draft, key, raw);
        if (ReferenceEquals(next, Draft))
        {
            return;
        }
        Draft = next;
        Changed?.Invoke();
    }

    /// <summary>操作を戻せるかです。</summary>
    public bool CanUndo => history.Count != 0 && Locks.Allows(Draft.Document, history[^1]);

    /// <summary>草稿の保存領域を分ける明示クラスタです。</summary>
    public string? ClusterId
    {
        get; private set;
    }

    /// <summary>ローカル保存版を別Peerへ混ぜない編集領域です。</summary>
    public string? PeerId
    {
        get; private set;
    }

    /// <summary>画面の再描画が必要な変更を通知します。</summary>
    public event Action? Changed;

    /// <summary>reload元の画面固有草稿を復元します。</summary>
    /// <returns>復元処理の完了です。</returns>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => initialization switch
    {
        { IsFaulted: false, IsCanceled: false } current => current,
        _ => initialization = InitializeCoreAsync(cancellationToken),
    };

    /// <summary>元の画面の保存元を一度だけ確定します。</summary>
    /// <returns>草稿復元の完了です。</returns>
    private async Task InitializeCoreAsync(CancellationToken cancellationToken = default)
    {
        (Guid Owner, string? Cluster, string? Peer, EditorDraft? Draft) restored = await EditorDraftStorage.InitializeAsync(storage, owner, cancellationToken).ConfigureAwait(false);
        owner = restored.Owner;
        ClusterId = restored.Cluster;
        PeerId = restored.Peer;
        if (restored.Draft is not null)
        {
            Draft = restored.Draft;
        }
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>異なるクラスタの同名ワークフローへ手元の変更を混ぜません。</summary>
    /// <returns>旧草稿の保存と選択クラスタの草稿復元です。</returns>
    public Task SwitchClusterAsync(string? clusterId, CancellationToken cancellationToken = default) => SwitchTargetAsync(clusterId, PeerId, cancellationToken);

    /// <summary>クラスタとPeerの組を変更し、前の草稿を保存してから対象側を復元します。</summary>
    /// <returns>保存と復元の完了です。</returns>
    public async Task SwitchTargetAsync(string? clusterId, string? peerId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        if (StorageProblem is not null && Draft.Dirty)
        {
            throw UiError.Attach(new InvalidOperationException("The current draft could not be saved. Export it before changing the connection."), "The current draft could not be saved. Export it before changing the connection.");
        }
        if (!string.Equals(ClusterId, clusterId, StringComparison.Ordinal) || !string.Equals(PeerId, peerId, StringComparison.Ordinal))
        {
            ClusterId = clusterId;
            PeerId = peerId;
            Locks.Update([]);
            Draft = EditorDraftNavigation.Empty();
            ResetSelection();
            Draft = await EditorDraftNavigation.RestoreTargetAsync(storage, owner, ClusterId, PeerId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Generation++;
        }
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>保存版を表示し、同じ画面の未保存変更があれば維持します。</summary>
    /// <returns>読み込みの完了です。</returns>
    public async Task LoadAsync(StoredWorkflow saved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saved);
        EditorDraft? retained = await EditorDraftStorage.ReadAsync(storage, owner, ClusterId, PeerId, saved.Definition.Id, cancellationToken).ConfigureAwait(false);
        Draft = retained is not null && retained.Dirty ? retained
                                                       : new(saved.Definition, saved.Definition, saved.Revision, saved.Definition, null, saved.Revision, false, [], LocalOnly: saved.LocalOnly);
        ResetSelection();
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>画面所有者が保管したclean版もオフラインで再選択します。</summary>
    /// <returns>手元に完全な草稿があったか。</returns>
    internal async Task<bool> RestoreRetainedAsync(string id, CancellationToken cancellationToken = default)
    {
        EditorDraft? retained = await EditorDraftStorage.ReadAsync(storage, owner, ClusterId, PeerId, id, cancellationToken).ConfigureAwait(false);
        if (retained is null || !string.Equals(retained.Document.Id, id, StringComparison.Ordinal))
        {
            return false;
        }
        Draft = retained;
        ResetSelection();
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
        return true;
    }

    /// <summary>同じ所有範囲の未保存新規草稿を復元します。</summary>
    /// <returns>新規草稿が見つかった場合だけtrueです。</returns>
    public Task<bool> TryOpenDraftAsync(string id, CancellationToken cancellationToken = default) => EditorDraftNavigation.TryOpenAsync(this, storage, owner, id, ApplyDraft, cancellationToken);

    /// <summary>保存済みの草稿へ表示を切り替えます。</summary>
    private void ApplyDraft(EditorDraft draft)
    {
        Draft = draft;
        ResetSelection();
        Changed?.Invoke();
    }

    /// <summary>新しい汎用定義を作成します。</summary>
    public void New(bool dashboard = false) => ApplyDraft(EditorDraftNavigation.Create(dashboard));

    /// <summary>保存した草稿を削除せず、現在の表示対象だけを閉じます。</summary>
    internal void Close() => ApplyDraft(EditorDraftNavigation.Empty());

    /// <summary>明示削除済みの現草稿を解除します。他画面の草稿は削除しません。</summary>
    /// <returns>この画面の保存解除です。</returns>
    public Task DiscardAsync(CancellationToken cancellationToken = default) => EditorDraftNavigation.DiscardAsync(this, storage, owner, cancellationToken);

    /// <summary>明示importを手元の変更として保持します。</summary>
    public void Import(WorkflowDefinition definition) => ApplyDraft(EditorDraftResolution.Imported(Draft.Document, definition));

    /// <summary>一回のUI変更とundo元を記録します。</summary>
    public void Change(Func<WorkflowDefinition, WorkflowDefinition> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        WorkflowDefinition edited = edit(Draft.Document);
        EditorDraftResolution.CheckKind(Draft.Document, edited);
        if (string.Equals(WorkflowDraftOrder.Revision(Draft.Document), WorkflowDraftOrder.Revision(edited), StringComparison.Ordinal) || !Locks.Allows(Draft.Document, edited))
        {
            return;
        }
        history.Add(ContractJson.Copy(Draft.Document));
        if (40 < history.Count)
        {
            history.RemoveAt(0);
        }
        if (!string.Equals(edited.Id, Draft.Document.Id, StringComparison.Ordinal))
        {
            Draft = new(edited, edited, null, edited, null, null, true, [], NeedsBaseline: true);
            ResetSelection();
        }
        else
        {
            string[] removed = Draft.Document.Nodes.Where(node => !edited.Nodes.Any(item => string.Equals(item.Id, node.Id, StringComparison.Ordinal))).SelectMany(node => new[] { node.Id + "/settings/", node.Id + "/inputs/" }).ToArray();
            Dictionary<string, string>? raw = Draft.InputDrafts?.Where(pair => !removed.Any(prefix => pair.Key.StartsWith(prefix, StringComparison.Ordinal))).ToDictionary(StringComparer.Ordinal);
            Draft = Draft with { Document = edited, Dirty = true, InputDrafts = raw };
            Selection.Retain(edited.Nodes.Select(node => node.Id));
        }
        Changed?.Invoke();
    }

    /// <summary>同じノードを識別子で更新します。</summary>
    public void ChangeNode(string id, Func<NodeDefinition, NodeDefinition> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Change(document => document with { Nodes = document.Nodes.Select(node => string.Equals(node.Id, id, StringComparison.Ordinal) ? edit(node)
                                                                                                                                      : node).ToArray() });
    }

    /// <summary>共有変更を壊さないよう再比較した履歴を戻します。</summary>
    public void Undo()
    {
        if (history.Count == 0 || !Locks.Allows(Draft.Document, history[^1]))
        {
            return;
        }
        Draft = Draft with { Document = history[^1], Dirty = true };
        history.RemoveAt(history.Count - 1);
        Selection.Retain(Draft.Document.Nodes.Select(node => node.Id));
        Changed?.Invoke();
    }

    /// <summary>ACKの比較元を保持し、送信中の追加編集を再baseします。</summary>
    public void Accept(DraftView view, WorkflowDefinition basis)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(basis);
        WorkflowDefinition before = Draft.Document;
        WireDefinition? selected = null;
        if (SelectedWire is int selectedIndex && 0 <= selectedIndex && selectedIndex < before.Wires.Count)
        {
            selected = before.Wires[selectedIndex];
        }
        WorkflowMergeResult merged = EditorDraftResolution.Merge(basis, view.Definition, before);
        WorkflowDefinition candidate = merged.Candidate ?? before;
        candidate = WorkflowDraftOrder.Same(candidate, before) ? before
                                                               : candidate;
        for (int index = history.Count - 1; !ReferenceEquals(candidate, before) && 0 <= index; index--)
        {
            WorkflowMergeResult undo = EditorDraftResolution.Merge(before, candidate, history[index]);
            if (undo.Conflicts.Count == 0 && undo.Candidate is not null)
            {
                history[index] = undo.Candidate;
            }
            else
            {
                history.RemoveAt(index);
            }
        }
        EditConflict[] conflicts = EditorDraftResolution.Conflicts(Draft, view, merged);
        Draft = EditorDraftResolution.Accepted(Draft, view, candidate, conflicts);
        SelectedWire = candidate.Wires.Select((wire, index) => (wire, index))
            .Where(item => selected is not null && (item.wire.FromNode, item.wire.FromPort, item.wire.ToNode, item.wire.ToPort).Equals((selected.FromNode, selected.FromPort, selected.ToNode, selected.ToPort)))
            .Select(item => (int?)item.index).FirstOrDefault();
        Selection.Retain(candidate.Nodes.Select(node => node.Id));
        Changed?.Invoke();
    }

    /// <summary>import元の比較baselineを取得後に固定します。</summary>
    public void SetBaseline(DraftView? view) => Draft = Draft with { Original = view?.Definition ?? Draft.Original, SessionVersion = view?.SessionVersion, DraftBaseRevision = view?.BaseRevision, Conflicts = view?.Conflicts ?? [], SharedConflicts = view?.Conflicts, NeedsBaseline = false };

    /// <summary>未知結果の要求を固定して維持します。</summary>
    public void Pending(DraftChangeRequest request) => Draft = Draft with { Pending = request };

    /// <summary>拒否された入力が修正された場合だけ旧pendingを解除します。</summary>
    public void ClearRejectedRequest() => Draft = Draft with { Pending = null };

    /// <summary>復元前に人が破棄した範囲を確定します。</summary>
    internal void DiscardResolved(DraftView view, StoredWorkflow saved, IReadOnlyList<string>? nodeIds)
    {
        Draft = EditorDraftResolution.Apply(Draft, view, saved, nodeIds);
        ResetSelection();
        Changed?.Invoke();
    }

    /// <summary>確認した保存版を反映します。</summary>
    public void Saved(string revision, WorkflowDefinition definition, bool localOnly = false, bool force = false)
    {
        Draft = EditorDraftResolution.Saved(Draft, revision, definition, localOnly, force);
        if (force)
        {
            Generation++;
        }
        Changed?.Invoke();
    }

    /// <summary>同じローカル基準版の統合結果だけを後続編集へ重ねます。</summary>
    public void Reintegrated(StoredWorkflow shared, string localRevision)
    {
        EditorDraft next = EditorDraftReintegration.Apply(Draft, shared, localRevision);
        if (ReferenceEquals(next, Draft))
        {
            return;
        }
        Draft = next;
        ResetSelection();
        Changed?.Invoke();
    }

    /// <summary>草稿を画面ごとの領域へ保存します。</summary>
    /// <returns>保存確認の完了です。</returns>
    public async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        StorageProblem = await EditorDraftStorage.WriteAsync(storage, owner, ClusterId, PeerId, Draft, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>画面切替で旧応答と選択を無効にします。</summary>
    private void ResetSelection()
    {
        Generation++;
        SelectedNode = null;
        SelectedWire = null;
        history.Clear();
    }

}
