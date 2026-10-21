using System.Net;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>下書きのHTTP同期を保存と分離し、遅延応答を拒否します。</summary>
internal sealed class EditorCollaboration(EditorDocument model, ManagementApi api)
{
    private bool active;
    private readonly EditorPresence positions = new(model, api);
    private readonly List<ValidationIssue> validation = [];
    private DraftView? buffered;
    private WorkflowDefinition? bufferedBasis;
    private int bufferedGeneration;
    private string? rejectedHash;
    private bool confirmed;
    private TaskCompletionSource<bool>? completion;

    /// <summary>座標表示だけを更新します。</summary>
    internal event Action? PresenceChanged
    {
        add => positions.Changed += value;
        remove => positions.Changed -= value;
    }

    /// <summary>追加オプションのリアルタイム座標共有です。</summary>
    internal bool RealtimePointer
    {
        get => positions.Realtime; set => positions.Realtime = value;
    }

    /// <summary>確定操作以外の移動は既定では公開しません。</summary>
    internal void SetPointer(NodePosition? point, string? node, NodePosition? position, bool interaction = false)
        => positions.SetPointer(point, node, position, interaction);

    /// <summary>値変更時の操作位置を確定します。</summary>
    internal void NotifyInteraction() => positions.Interact();

    /// <summary>編集本文とは独立した座標・選択を同期します。</summary>
    /// <returns>表示の同期です。</returns>
    internal Task SynchronizePresenceAsync(string hostName, CancellationToken cancellationToken = default)
        => positions.RefreshAsync(hostName, cancellationToken);

    /// <summary>この画面実体です。</summary>
    public Guid UiInstanceId => positions.Id;

    /// <summary>記録とポインタ表示に使う編集者名です。</summary>
    public string DisplayName
    {
        get => positions.Name;
        set
        {
            positions.Name = value;
            api.DisplayName = value;
        }
    }

    /// <summary>接続先の確認であり全Peer到達の保証ではありません。</summary>
    public string Status
    {
        get; private set;
    } = "共同編集へ接続中";

    /// <summary>期限内としてAPIが報告した編集者情報です。</summary>
    public IReadOnlyList<PresenceView> Presence => positions.Items;

    /// <summary>サーバーが拒否した入力の理由です。</summary>
    public IReadOnlyList<ValidationIssue> Validation => validation;

    /// <summary>開始済みの同期要求があるかです。</summary>
    public bool Active => active;

    /// <summary>保存操作は開始済みの草稿照合が完了するまで待ちます。</summary>
    /// <returns>現在の照合の完了です。</returns>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        if (active && completion is not null)
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>草稿の未到達・未解消状態です。</summary>
    public bool Pending => model.Draft.Pending is not null || buffered is not null || !Same(model.Draft.Original, model.Draft.Document);

    /// <summary>型付き草稿と編集者情報を現在Peerで照合します。</summary>
    /// <returns>一回の確認完了です。</returns>
    public async Task SynchronizeAsync(bool canApply, string hostName, CancellationToken cancellationToken = default)
    {
        if (active)
        {
            return;
        }
        active = true;
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation = model.Generation;
        string id = model.Draft.Document.Id;
        string path = "/api/collaboration/" + Uri.EscapeDataString(id);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = timeout.Token;
        try
        {
            if (bufferedGeneration != generation)
            {
                buffered = null;
                positions.Clear();
                confirmed = false;
                bufferedGeneration = generation;
            }
            if (model.Draft.SessionVersion is { } version && !string.Equals(version, WorkflowDraftOrder.Revision(model.Draft.Original), StringComparison.Ordinal))
            {
                if (!canApply)
                {
                    confirmed = false;
                    Status = "入力終了後に草稿の比較元を再確認します。";
                    return;
                }
                DraftView? repaired = await ReadDraftAsync(path, cancellationToken).ConfigureAwait(false);
                if (generation != model.Generation || repaired is null)
                {
                    return;
                }
                model.Accept(repaired, model.Draft.Original);
                buffered = null;
                rejectedHash = null;
                validation.Clear();
            }
            if (rejectedHash is not null && !string.Equals(rejectedHash, WorkflowDraftOrder.Revision(model.Draft.Document), StringComparison.Ordinal))
            {
                model.ClearRejectedRequest();
                rejectedHash = null;
                validation.Clear();
            }
            if (buffered is not null && CanAccept(buffered, bufferedBasis!, canApply))
            {
                model.Accept(buffered, bufferedBasis!);
                buffered = null;
            }
            if (buffered is null && (rejectedHash is null || !string.Equals(rejectedHash, WorkflowDraftOrder.Revision(model.Draft.Document), StringComparison.Ordinal)))
            {
                if (model.Draft.NeedsBaseline)
                {
                    DraftView? baseline = await ReadDraftAsync(path, cancellationToken).ConfigureAwait(false);
                    if (generation != model.Generation)
                    {
                        return;
                    }
                    model.SetBaseline(baseline);
                }
                EditorDraft draft = model.Draft;
                if (draft.Pending is null && draft.Conflicts.Count == 0 && (!Same(draft.Original, draft.Document) || draft.SessionVersion is null && draft.BaseRevision is null))
                {
                    model.Pending(Request(new Dictionary<string, EEditResolution>()));
                    await model.PersistAsync(cancellationToken).ConfigureAwait(false);
                }
                DraftChangeRequest? request = model.Draft.Pending;
                DraftView? view = request is null ? await ReadDraftAsync(path, cancellationToken).ConfigureAwait(false)
                                                  : await api.PostAsync<DraftView>(path + "/changes", request, cancellationToken).ConfigureAwait(false);
                if (generation != model.Generation || !string.Equals(id, model.Draft.Document.Id, StringComparison.Ordinal))
                {
                    return;
                }
                if (view is not null)
                {
                    if (!string.Equals(view.WorkflowId, id, StringComparison.Ordinal) || !string.Equals(view.Definition.Id, id, StringComparison.Ordinal))
                    {
                        throw UiError.Attach(new InvalidOperationException("The workflow in the response does not match."), "The workflow in the response does not match.");
                    }
                    validation.Clear();
                    validation.AddRange(view.Validation);
                    if (Validation.Count != 0)
                    {
                        rejectedHash = WorkflowDraftOrder.Revision(model.Draft.Document);
                    }
                    else
                    {
                        if (request is not null || draft.SessionVersion is null
                            || !string.Equals(draft.SessionVersion, view.SessionVersion, StringComparison.Ordinal)
                            || !string.Equals(draft.DraftBaseRevision, view.BaseRevision, StringComparison.Ordinal)
                            || draft.Conflicts.Count != 0 || view.Conflicts.Count != 0)
                        {
                            WorkflowDefinition basis = request?.Edited ?? draft.Original;
                            if (CanAccept(view, basis, canApply))
                            {
                                model.Accept(view, basis);
                            }
                            else
                            {
                                buffered = view;
                                bufferedBasis = basis;
                                bufferedGeneration = generation;
                            }
                        }
                    }
                }
            }
            await SynchronizePresenceAsync(hostName, cancellationToken).ConfigureAwait(false);
            if (generation == model.Generation)
            {
                confirmed = Validation.Count == 0;
                Status = "接続先で草稿を確認（全Peer到達は未保証）";
                if (Validation.Count != 0)
                {
                    Status = "共同編集の入力が拒否されました。検証結果を確認してください。";
                }
                if (Validation.Count == 0 && buffered is not null)
                {
                    Status = "入力終了後に他画面の変更を反映";
                }
                await model.PersistAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (generation == model.Generation)
            {
                confirmed = false;
                Status = "共同編集未確認。端末の草稿と元要求を保持しています。";
            }
        }
        finally
        {
            active = false;
            completion.TrySetResult(true);
        }
    }

    /// <summary>入力欄にフォーカス中も、編集中の箇所以外の共有変更を受信します。</summary>
    internal bool AllowUnfocusedUpdates
    {
        get; set;
    }

    /// <summary>編集中の名前・ノード・接続を触らない変更だけはフォーカス解除を待ちません。</summary>
    /// <returns>この表示へ反映してよい候補かです。</returns>
    private bool CanAccept(DraftView view, WorkflowDefinition basis, bool canApply)
    {
        if (canApply)
        {
            return true;
        }
        WorkflowDefinition current = model.Draft.Document;
        WorkflowDefinition? candidate = EditorDraftResolution.Merge(basis, view.Definition, current).Candidate;
        if (candidate is null)
        {
            return false;
        }
        if (Same(candidate, current))
        {
            return true;
        }
        if (!AllowUnfocusedUpdates || !string.Equals(candidate.Name, current.Name, StringComparison.Ordinal))
        {
            return false;
        }
        if (model.SelectedNode is { } selected && !string.Equals(
            ContractJson.Hash(current.Nodes.FirstOrDefault(node => string.Equals(node.Id, selected, StringComparison.Ordinal))),
            ContractJson.Hash(candidate.Nodes.FirstOrDefault(node => string.Equals(node.Id, selected, StringComparison.Ordinal))), StringComparison.Ordinal))
        {
            return false;
        }
        if (model.SelectedWire is int index && 0 <= index && index < current.Wires.Count)
        {
            WireDefinition wire = current.Wires[index];
            return candidate.Wires.Contains(wire);
        }
        return true;
    }

    /// <summary>保存前に未確認・未解消を拒否します。</summary>
    public void EnsureConfirmed()
    {
        if (!confirmed || active || Pending || model.Draft.Conflicts.Count != 0 || Validation.Count != 0)
        {
            throw UiError.Attach(new InvalidOperationException("Collaborative editing has not been confirmed, or conflicts still need to be resolved."), "Collaborative editing has not been confirmed, or conflicts still need to be resolved.");
        }
    }

    /// <summary>比較した候補を選択し同じ共有境界へ返します。</summary>
    public void Resolve(IReadOnlyList<EditConflict> expected, IReadOnlyDictionary<string, int> choices)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(choices);
        if (active || buffered is not null || !string.Equals(ContractJson.Hash(expected), ContractJson.Hash(model.Draft.Conflicts), StringComparison.Ordinal))
        {
            throw UiError.Attach(new InvalidOperationException("The candidates have changed. Reopen the comparison."), "The candidates have changed. Reopen the comparison.");
        }
        WorkflowDefinition edited = ConflictSelection.Apply(model.Draft.Document, expected, choices);
        model.Change(_ => edited);
        model.Pending(Request(choices.ToDictionary(pair => pair.Key, pair => pair.Value == -1 ? EEditResolution.KeepCurrent
                                                                                              : EEditResolution.UseEdited, StringComparer.Ordinal)));
    }

    /// <summary>未保存の新規ワークフローだけ404を許容します。</summary>
    /// <returns>確認した草稿です。</returns>
    private async Task<DraftView?> ReadDraftAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await api.GetAsync<DraftView>(path + "/draft", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>取得開始時の入力を所有コピーで固定します。</summary>
    /// <returns>再送しても変化しない要求です。</returns>
    private DraftChangeRequest Request(IReadOnlyDictionary<string, EEditResolution> resolutions)
    {
        EditorDraft draft = model.Draft;
        return ContractJson.Copy(new DraftChangeRequest(Guid.NewGuid(), draft.Document.Id, UiInstanceId, draft.DraftBaseRevision, draft.SessionVersion, draft.Original, draft.Document, resolutions));
    }

    /// <summary>値の精度と構造を共通契約で比較します。</summary>
    /// <returns>同一の内容かです。</returns>
    private static bool Same(WorkflowDefinition first, WorkflowDefinition second) => WorkflowDraftOrder.Same(first, second);
}
