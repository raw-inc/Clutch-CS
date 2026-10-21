using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Snapshots;

/// <summary>復元時に隔離された編集中の草稿を確認し、解消方法を選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class SnapshotRestoreReview
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get;
        set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    [Inject]
    private EditorCollaboration Collaboration
    {
        get;
        set;
    } = null!;

    /// <summary>確認対象となる固定済みの復元要求です。</summary>
    [Parameter]
    public SnapshotRestoreRequest? Request
    {
        get;
        set;
    }

    /// <summary>確認画面の終了を親へ通知します。</summary>
    [Parameter]
    public EventCallback Closed
    {
        get;
        set;
    }

    private Guid? initialized;
    private SnapshotRestorePreparation? preparation;
    private bool busy;
    private Func<string>? message = () => string.Empty;

    /// <summary>親から渡された対象に合わせて関連する状態を再確認します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnParametersSetAsync()
    {
        if (Request is not null && initialized != Request.OperationId)
        {
            initialized = Request.OperationId;
            preparation = null;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>保留した草稿を現在の状態で解消できるか判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool CanResolve(SnapshotEditorState editor) => editor.Presence.Any(item => item.Presence.UiInstanceId == Collaboration.UiInstanceId) || editor.Presence.All(item => !item.Presence.Dirty);

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshAsync()
    {
        if (Request is null)
        {
            return;
        }

        try
        {
            await Model.InitializeAsync(cancellationToken: UiCancellation).ConfigureAwait(true);
            if (Request.Selections.Any(item => string.Equals(item.Target.Id, Model.Draft.Document.Id, StringComparison.Ordinal)) && Model.Draft.Dirty)
            {
                await Collaboration.SynchronizeAsync(false, Api.SelectedPeerId ?? "browser", cancellationToken: UiCancellation).ConfigureAwait(true);
            }

            preparation = await Api.PostAsync<SnapshotRestorePreparation>("/api/snapshots/prepare", Request, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (preparation?.OperationId != Request.OperationId || !string.Equals(preparation.Fingerprint, ContractJson.Hash(Request), StringComparison.Ordinal))
            {
                throw UiError.InvalidOperation("This result does not belong to the original restore request.");
            }

            message = preparation.Ready ? Ui.Deferred("No unsaved edits block this restore. This is checked again immediately before starting.")
                                        : (preparation.Reason) is null ? Ui.Deferred("Check unsaved edits or the restore already in progress.")
                                                                       : Ui.Deferred("{0}", preparation.Reason);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or JSException or OperationCanceledException)
        {
            preparation = null;
            message = Ui.Deferred("Pre-restore state could not be confirmed. Restore has not started.");
        }
    }

    /// <summary>復元確認中の草稿を保存してから解消状態を再確認します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SaveAsync(SnapshotEditorState editor)
    {
        busy = true;
        try
        {
            if (Model.HasInvalidInputs || Model.Draft.Conflicts.Count != 0)
            {
                throw UiError.InvalidOperation("Resolve unconfirmed input and conflicts in the editor.");
            }

            await Collaboration.WaitForIdleAsync(cancellationToken: UiCancellation).ConfigureAwait(true);
            await Collaboration.SynchronizeAsync(true, Api.SelectedPeerId ?? "browser", cancellationToken: UiCancellation).ConfigureAwait(true);
            Collaboration.EnsureConfirmed();
            EditorDraft? draft = Model.Draft;
            WorkflowSaveRequest? save = new WorkflowSaveRequest(Guid.NewGuid(), draft.Document, draft.BaseRevision, EActivationPolicy.Explicit);
            JsonElement value = await Journal.StartAsync(save.OperationId, "Save draft before restore", "/api/workflows/save", "/api/workflows/operations/" + save.OperationId, save, cancellationToken: UiCancellation).ConfigureAwait(true);
            WorkflowSaveResult? result = value.Deserialize<WorkflowSaveResult>(ContractJson.Options);
            if (result?.Saved.Outcome != EOutcome.Succeeded || result.Shared.Outcome != EOutcome.Succeeded)
            {
                throw UiError.InvalidOperation("Save and share success is unconfirmed. Check revisions and conflicts in the editor.");
            }

            Model.Saved(result.Revision!, draft.Document);
            await Model.PersistAsync(cancellationToken: UiCancellation).ConfigureAwait(true);
            await Collaboration.SynchronizeAsync(false, Api.SelectedPeerId ?? "browser", cancellationToken: UiCancellation).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            SnapshotEditorState? current = preparation?.Editors.FirstOrDefault(item => string.Equals(item.PeerId, editor.PeerId, StringComparison.Ordinal) && string.Equals(item.WorkflowId, editor.WorkflowId, StringComparison.Ordinal));
            if (current is not null)
            {
                await ResolveAsync(current, ESnapshotDraftAction.ConfirmSaved).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or JSException or OperationCanceledException)
        {
            message = exception is InvalidOperationException ? Ui.DeferredException(exception)
                                                             : Ui.Deferred("The draft-save result is unconfirmed. Query the original ID in operation history.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>確認した競合または実体の解消方法を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ResolveAsync(SnapshotEditorState editor, ESnapshotDraftAction action)
    {
        if (Request is null || !CanResolve(editor))
        {
            return;
        }

        busy = true;
        try
        {
            SnapshotDraftResolution? resolution = new SnapshotDraftResolution(Request, editor.PeerId, editor.WorkflowId, Collaboration.UiInstanceId, editor.SessionVersion, action);
            ConfigurationPhaseResult? result = await Api.PostAsync<ConfigurationPhaseResult>("/api/snapshots/resolve", resolution, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (result?.Outcome != EOutcome.Succeeded)
            {
                throw new InvalidOperationException(result?.Reason ?? "Edit confirmation is unconfirmed.");
            }

            if (action == ESnapshotDraftAction.Discard && string.Equals(Model.Draft.Document.Id, editor.WorkflowId, StringComparison.Ordinal) && string.Equals(Api.SelectedPeerId, editor.PeerId, StringComparison.Ordinal))
            {
                string? path = Uri.EscapeDataString(editor.WorkflowId);
                DraftView? draft = await Api.GetAsync<DraftView>("/api/collaboration/" + path + "/draft", cancellationToken: UiCancellation).ConfigureAwait(true);
                WorkflowView? saved = await WorkflowReader.ReadAsync(Api, editor.WorkflowId, cancellationToken: UiCancellation).ConfigureAwait(true);
                if (draft is null || saved is null)
                {
                    throw UiError.InvalidOperation("The comparison base after discard could not be retrieved. Recheck state in the editor.");
                }

                Model.DiscardResolved(draft, saved.RequireSaved(), editor.NodeIds);
                await Model.PersistAsync(cancellationToken: UiCancellation).ConfigureAwait(true);
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = exception is InvalidOperationException ? Ui.DeferredException(exception)
                                                             : Ui.Deferred("The edit-confirmation result is unknown. Check the original restore request again.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>確認済みの草稿解消結果で復元操作を開始します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task StartAsync()
    {
        if (Request is null || preparation?.Ready != true || busy)
        {
            return;
        }

        busy = true;
        try
        {
            await Journal.StartAsync(Request.OperationId, "Restore snapshot", "/api/snapshots/restore", "/api/snapshots/restores/" + Request.OperationId, Request, "/api/snapshots/recover", cancellationToken: UiCancellation).ConfigureAwait(true);
            await Closed.InvokeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or JSException or OperationCanceledException)
        {
            message = Ui.Deferred("Restore {0} is unconfirmed. Query or recover the original operation in history; do not resend as a new restore.", Request.OperationId);
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>実行前の復元確認を取り消して親画面へ通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task CancelAsync() => await Closed.InvokeAsync().ConfigureAwait(true);
}
