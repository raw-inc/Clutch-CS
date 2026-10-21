using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>離脱中のローカルワークフローと共有版の統合を管理します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class DetachedWorkflowPanel
{
    private static string MatchDetachedDiscarded => "Detached.Discarded";

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

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<DetachedWorkflowListSummary> entries = [];
    private readonly Dictionary<string, EEditResolution?> choices = new(StringComparer.Ordinal);
    private DetachedWorkflowState? state;
    private WorkflowMergeResult? candidate;
    private WorkflowSaveResult? saved;
    private EActivationPolicy activation = EActivationPolicy.Auto;
    private string? cursor, cluster, peer, operationCluster, operationPeer;
    private Func<string>? message;
    private Guid? operation;
    private bool busy, loaded, discardOpen;
    private bool SameConnection => string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(peer, Api.SelectedPeerId, StringComparison.Ordinal);

    /// <summary>表示対象の一覧または状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadAsync(bool next)
    {
        busy = true;
        message = null;
        (string? SelectedClusterId, string? SelectedPeerId) target = (Api.SelectedClusterId, Api.SelectedPeerId);
        try
        {
            DetachedWorkflowListPage? page = await Api.PostAsync<DetachedWorkflowListPage>("/api/workflows/detached/query", new DetachedWorkflowListQuery(Cursor: next ? cursor
                                                                                                                                                                       : null), cancellationToken: UiCancellation).ConfigureAwait(true);
            if (target != (Api.SelectedClusterId, Api.SelectedPeerId))
            {
                return;
            }

            entries = page?.Entries ?? throw UiError.InvalidOperation("The list is unconfirmed.");
            cursor = page.Cursor;
            (cluster, peer) = target;
            loaded = true;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("The local change list could not be confirmed. Restart from the first page if candidates or expiry changed.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>対象の現在値を取得して編集または選択画面を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenAsync(DetachedWorkflowListSummary summary)
    {
        busy = true;
        message = null;
        try
        {
            (string? cluster, string? peer) target = (cluster, peer);
            DetachedWorkflowDetail? detail = await Api.GetAsync<DetachedWorkflowDetail>("/api/workflows/" + Uri.EscapeDataString(summary.WorkflowId) + "/detached", cancellationToken: UiCancellation).ConfigureAwait(true);
            if (target != (Api.SelectedClusterId, Api.SelectedPeerId))
            {
                return;
            }

            if (detail?.State is not { } found)
            {
                message = (detail?.DetailReason) is null ? Ui.Deferred("Current local changes could not be confirmed.")
                                                         : Ui.Deferred("{0}", detail?.DetailReason);
                return;
            }

            if (!string.Equals(found.WorkflowId, summary.WorkflowId, StringComparison.Ordinal) || !string.Equals(found.Local.Revision, summary.LocalRevision, StringComparison.Ordinal))
            {
                throw UiError.InvalidOperation("The local revision changed.");
            }

            state = found;
            choices.Clear();
            activation = found.Local.ActivationPolicy;
            operation = found.IntegrationOperationId;
            saved = found.Integration;
            operationCluster = cluster;
            operationPeer = peer;
            Recalculate();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("The local comparison changed or could not be retrieved. Refresh the list.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>指定した候補を現在の選択へ反映します。</summary>
    private void Choose(string path, ChangeEventArgs args)
    {
        choices[path] = Enum.TryParse<EEditResolution>(args.Value?.ToString(), out EEditResolution choice) ? choice
                                                                                                           : null;
        Recalculate();
    }

    /// <summary>採用候補の選択に合わせて統合結果を再計算します。</summary>
    private void Recalculate()
    {
        if (state is not null)
        {
            candidate = DetachedWorkflowComparison.Merge(state.Base, state.Current, state.Local, Guid.Empty, Selections());
        }
    }

    /// <summary>競合箇所ごとの選択を解消要求に使用する値へ変換します。</summary>
    /// <returns>選択が確定した競合パスと採用方針の辞書です。</returns>
    private Dictionary<string, EEditResolution> Selections() => choices.Where(item => item.Value is not null).ToDictionary(item => item.Key, item => item.Value!.Value, StringComparer.Ordinal);

    /// <summary>確認した競合または実体の解消方法を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ResolveAsync(bool discard)
    {
        if (state is null || state.IntegrationOperationId is not null && !state.IntegrationRetryAllowed || !SameConnection || busy)
        {
            return;
        }

        DetachedWorkflowState? selected = state;
        DetachedWorkflowResolution? request = new DetachedWorkflowResolution(Guid.NewGuid(), selected.WorkflowId, selected.Local.Revision, Selections(), discard, activation, selected.Current?.Revision, true);
        if (!discard && (candidate?.Candidate is null || candidate.Conflicts.Count != 0))
        {
            message = Ui.Deferred("Select every conflict. Nothing was sent.");
            return;
        }

        busy = true;
        message = null;
        operation = request.OperationId;
        operationCluster = cluster;
        operationPeer = peer;
        discardOpen = false;
        saved = null;
        try
        {
            JsonElement response = await Journal.StartAsync(request.OperationId, "Integrate detached changes", "/api/workflows/detached/resolve", "/api/workflows/detached/operations/" + request.OperationId, request, cancellationToken: UiCancellation).ConfigureAwait(true);
            WorkflowMergeResult? result = response.Deserialize<WorkflowMergeResult>(ContractJson.Options) ?? throw UiError.InvalidOperation("The result is unconfirmed.");
            saved = result.Save;
            if (saved?.OperationId != request.OperationId)
            {
                throw UiError.InvalidOperation("The original result could not be confirmed.");
            }

            if (saved.Shared.Outcome == EOutcome.Succeeded && result.Candidate is not null && SameConnection)
            {
                Model.Reintegrated(new StoredWorkflow(result.Candidate, saved.Revision!, DateTimeOffset.UtcNow, activation), selected.Local.Revision);
            }

            if (discard && saved.Saved.Outcome == EOutcome.Succeeded && string.Equals(saved.Shared.Reason, MatchDetachedDiscarded, StringComparison.Ordinal) && SameConnection)
            {
                if (selected.Current is not null)
                {
                    Model.Reintegrated(selected.Current, selected.Local.Revision);
                }
                else
                {
                    if (string.Equals(Model.Draft.Document.Id, selected.WorkflowId, StringComparison.Ordinal) && !Model.Draft.Dirty)
                    {
                        Model.New();
                    }
                }
            }

            state = null;
            message = Ui.Deferred("Check the original saved, shared and applied results. Unconfirmed phases are not treated as integrated.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or JSException)
        {
            message = Ui.Deferred("The result is unconfirmed. Keep the original request and ID, and query it instead of starting another integration.");
            state = null;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>新たな操作を作らず元の操作IDの結果を照会します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadOperationAsync()
    {
        if (operation is not Guid id)
        {
            return;
        }

        busy = true;
        try
        {
            WorkflowSaveResult? result = await Api.GetForPeerAsync<WorkflowSaveResult>("/api/workflows/detached/operations/" + id, operationPeer, operationCluster, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (result?.OperationId != id)
            {
                throw UiError.InvalidOperation("The original result is unconfirmed.");
            }

            saved = result;
            message = Ui.Deferred("The original operation was queried. No new integration or cancellation was sent.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("The original integration result is still unconfirmed.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>比較済みの基準版と候補から解消要求を作ります。</summary>
    /// <returns>基準版と解消方針を固定したローカルワークフローの統合要求です。</returns>
    internal static DetachedWorkflowResolution CreateResolution(DetachedWorkflowState state, IReadOnlyDictionary<string, EEditResolution> choices, bool discard, EActivationPolicy activation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(choices);
        if (state.IntegrationOperationId is not null && !state.IntegrationRetryAllowed)
        {
            throw UiError.InvalidOperation("Query the original integration operation.");
        }

        WorkflowMergeResult? preview = DetachedWorkflowComparison.Merge(state.Base, state.Current, state.Local, Guid.Empty, choices);
        if (!discard && (preview.Conflicts.Count != 0 || preview.Candidate is null))
        {
            throw UiError.InvalidOperation("Select every conflict.");
        }

        return new(Guid.NewGuid(), state.WorkflowId, state.Local.Revision, choices, discard, activation, state.Current?.Revision, true);
    }

    /// <summary>ローカルワークフローの統合確認を取り消します。</summary>
    private void Cancel()
    {
        if (!busy)
        {
            state = null;
            discardOpen = false;
        }
    }

    /// <summary>比較する内容を表示用の文字列へ変換します。</summary>
    /// <returns>型と欠損状態を区別した比較用の表示です。</returns>
    private string Preview(WorkflowDefinition? definition) => definition is null ? Ui["(deleted, not created, or unconfirmed)"]
                                                                                 : Limit(JsonSerializer.Serialize(definition, ContractJson.Options));

    /// <summary>候補の実値を比較表示用に整形します。</summary>
    /// <returns>欠損・削除と実値を区別した比較用の表示です。</returns>
    private string ValuePreview(JsonElement? value, bool? exists) => exists == false ? Ui["(missing or deleted)"]
                                                                                     : value is null && exists is null ? Ui["(null and deletion are not distinguished)"]
                                                                                                                       : Limit(value?.GetRawText() ?? "null");

    /// <summary>比較表示する長文を有限の長さに制限します。</summary>
    /// <returns>先頭4096文字以内と必要時の省略通知です。</returns>
    private string Limit(string value) => value.Length <= 4096 ? value
                                                               : value[..4096] + "\n…" + Ui["Display limited to 4096 characters; selection uses the complete candidate."];
}
