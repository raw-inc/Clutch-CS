using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Execution;

/// <summary>対象範囲と確認内容を固定して安全操作を要求します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class SafetyPanel
{
    private static string MatchOperationId => "operationId";

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    private bool open, busy, confirmOpen;
    private Func<string>? message;
    private SafetyView? status;
    private ManualSafetyPreview? preview;
    private ManualSafetyChange? pending;
    private JsonElement? result;
    private Guid? lastOperation;
    private readonly List<(string Workflow, string Scope, string Kind)> targets = [];
    private int selected;
    private string? targetWorkflow;
    private bool targetsLoaded;
    private ManualSafetyStatus? Current => targets.Count == 0 ? null
                                                              : status?.States.FirstOrDefault(item => string.Equals(item.WorkflowId, targets[selected].Workflow, StringComparison.Ordinal) && string.Equals(item.Scope, targets[selected].Scope, StringComparison.Ordinal));

    private string ConfirmationAction => pending switch
    {
        { Stopped: true } => "Pause new inputs",
        { Continued: true } => "Allow new inputs temporarily",
        _ => "Restore normal safety behavior"
    };

    private string OverrideState => (status?.Loaded == true, targets.Count, Current) switch
    {
        (false, _, _) => "Not checked",
        (_, 0, _) when targetsLoaded => "No eligible targets",
        (_, 0, _) => "No target selected",
        (_, _, { Stopped: true }) => "New inputs stopped",
        (_, _, { Continued: true }) => "Temporary continuation past an automatic stop",
        _ => "No temporary override"
    };

    /// <summary>対象の現在値を取得して編集または選択画面を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenAsync()
    {
        try
        {
            status = await Api.GetAsync<SafetyView>("/api/safety", cancellationToken: UiCancellation).ConfigureAwait(true);
            if (status?.Loaded != true)
            {
                throw UiError.InvalidOperation("Safety state has not been retrieved.");
            }

            targets.Clear();
            targetsLoaded = false;
            targetWorkflow = null;
            selected = 0;
            preview = null;
            open = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            status = null;
            targets.Clear();
            targetsLoaded = false;
            targetWorkflow = null;
            message = Ui.Deferred("Safety state and effective targets could not be confirmed.");
        }
    }

    /// <summary>操作対象にできるワークフローやノードの候補を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadTargetsAsync(WorkflowListSummary summary)
    {
        try
        {
            WorkflowView? workflow = await WorkflowReader.ReadAsync(Api, summary.Id!, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (workflow?.Effective is null)
            {
                throw new InvalidOperationException(workflow?.DetailReason ?? "There is no effective revision.");
            }

            targets.Clear();
            HashSet<string> nodes = workflow.Effective.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> groups = workflow.Effective.Nodes.Select(node => node.Safety?.Group).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (NodeDefinition node in workflow.Effective.Nodes)
            {
                string kind = groups.Contains(node.Id) ? "Node and safety group"
                                                       : "Node";
                targets.Add((workflow.Effective.Id, node.Id, kind));
            }
            foreach (string group in groups.Where(group => !nodes.Contains(group)))
            {
                targets.Add((workflow.Effective.Id, group, "Safety group"));
            }

            selected = 0;
            targetsLoaded = true;
            targetWorkflow = summary.Name;
            preview = null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            targets.Clear();
            targetsLoaded = false;
            targetWorkflow = null;
            message = Ui.Deferred("The complete target node list could not be retrieved.{0}", exception.Message);
        }
    }

    /// <summary>選択した変更内容を対象範囲へ反映する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ChangeAsync(bool stopped, bool continued)
    {
        if (busy || targets.Count == 0)
        {
            return;
        }

        busy = true;
        pending = null;
        try
        {
            (string Workflow, string Scope, string Kind) target = targets[selected];
            ManualSafetyChange? request = new ManualSafetyChange(Guid.NewGuid(), target.Workflow, target.Scope, stopped, Current?.Version, continued);
            preview = await Api.PostAsync<ManualSafetyPreview>("/api/safety/preview", request, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (preview is null || !string.Equals(preview.WorkflowId, request.WorkflowId, StringComparison.Ordinal) || !string.Equals(preview.Scope, request.Scope, StringComparison.Ordinal))
            {
                throw UiError.InvalidOperation("The target preview could not be confirmed.");
            }

            pending = request with
            {
                ExpectedWorkflowRevision = preview.WorkflowRevision
            };
            if (preview.ConfirmationRequired)
            {
                confirmOpen = true;
            }
            else
            {
                await ApplyAsync(pending).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            message = Ui.Deferred("The preview or apply result is unconfirmed. Refresh current state; the request is not resent automatically.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>対象と影響を固定して実行前の確認状態を作ります。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ConfirmAsync()
    {
        if (pending is null || busy)
        {
            return;
        }

        busy = true;
        try
        {
            await ApplyAsync(pending with
            {
                ImpactConfirmed = true
            }).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            message = Ui.Deferred("The apply result is unconfirmed. Refresh current state; the request is not resent automatically.");
        }
        finally
        {
            busy = false;
            confirmOpen = false;
        }
    }

    /// <summary>確認した選択内容に基づく操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ApplyAsync(ManualSafetyChange request)
    {
        lastOperation = request.OperationId;
        result = await Api.PostAsync<JsonElement>("/api/v2/safety", request, cancellationToken: UiCancellation).ConfigureAwait(true);
        if (result is JsonElement value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(MatchOperationId, out JsonElement identity) && identity.GetGuid() != request.OperationId)
        {
            result = null;
            throw UiError.InvalidOperation("The response does not match this operation ID.");
        }

        message = Ui.Deferred("Saved / accepted: {0}. This does not confirm an already-started execution stopped.", OperationJournal.Outcome(result));
        pending = null;
        status = await Api.GetAsync<SafetyView>("/api/safety", cancellationToken: UiCancellation).ConfigureAwait(true);
        preview = await Api.PostAsync<ManualSafetyPreview>("/api/safety/preview", request, cancellationToken: UiCancellation).ConfigureAwait(true);
    }

    /// <summary>未実行の確認内容を破棄して編集に戻ります。</summary>
    private void CancelConfirmation()
    {
        confirmOpen = false;
        pending = null;
    }

    /// <summary>新たな操作を作らず元の操作IDの結果を照会します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadOperationAsync()
    {
        if (lastOperation is not Guid id || busy)
        {
            return;
        }

        busy = true;
        try
        {
            result = await Api.GetAsync<JsonElement>("/api/safety/operations/" + id, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (result is JsonElement value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(MatchOperationId, out JsonElement identity) && identity.GetGuid() != id)
            {
                throw new InvalidOperationException();
            }

            message = Ui.Deferred("Acceptance result: {0}", OperationJournal.Outcome(result));
            status = await Api.GetAsync<SafetyView>("/api/safety", cancellationToken: UiCancellation).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            result = null;
            message = Ui.Deferred("The acceptance result is unconfirmed. Query the same ID without executing again.");
        }
        finally
        {
            busy = false;
        }
    }
}
