using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

namespace ReArtWord.Contents.BackgroundService.Client.Observability;

/// <summary>実行の詳細追跡と停止・抑止に関する操作を提供します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class TracePanel
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

    private TraceView? state;
    private string workflow = string.Empty;
    private Func<string>? message = () => string.Empty;
    private bool inputs = true;
    private bool outputs = true;
    private bool raw = true;
    private string rawLimit = string.Empty;
    private bool stopOpen;

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshAsync()
    {
        try
        {
            state = await Api.GetAsync<TraceView>("/api/trace", cancellationToken: UiCancellation).ConfigureAwait(true);
            if (!state?.States.Any(item => string.Equals(item.WorkflowId, workflow, StringComparison.Ordinal)) ?? true)
            {
                workflow = state is { States.Count: > 0 } ? state.States[0].WorkflowId
                                                          : string.Empty;
            }

            message = state?.Loaded == true ? Ui.Deferred("The connected peer state was retrieved.")
                                            : Ui.Deferred("Trace state is unconfirmed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            message = Ui.Deferred("Trace state could not be retrieved.");
        }
    }

    /// <summary>指定範囲の詳細追跡を開始する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task EnableAsync() => ChangeAsync(true);

    /// <summary>指定範囲の詳細追跡を解除する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task DisableAsync() => ChangeAsync(false);

    /// <summary>選択した変更内容を対象範囲へ反映する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ChangeAsync(bool enabled)
    {
        if (state?.Loaded != true || string.IsNullOrWhiteSpace(workflow))
        {
            message = Ui.Deferred("Choose a retrieved workflow and capture scope.");
            return;
        }

        int? bytes = null;
        try
        {
            if (rawLimit.Length != 0)
            {
                bytes = FormInput.Integer(rawLimit, "Leading byte count");
            }
        }
        catch (InvalidOperationException exception)
        {
            message = Ui.DeferredException(exception);
            return;
        }

        TraceStatus? current = state.States.FirstOrDefault(item => string.Equals(item.WorkflowId, workflow, StringComparison.Ordinal));
        TraceChange? request = new TraceChange(Guid.NewGuid(), workflow, enabled, current?.Version, new(inputs, outputs, raw, bytes));
        await SendAsync(request.OperationId, "Change temporary tracing", "/api/trace", "/api/trace/operations/", request).ConfigureAwait(true);
    }

    /// <summary>選択した対象の停止を要求し、元の操作結果を保持します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task StopAsync()
    {
        if (state?.Loaded != true)
        {
            return;
        }

        TraceClusterStopRequest? request = new TraceClusterStopRequest(Guid.NewGuid(), state.Version, state.Targets);
        stopOpen = false;
        await SendAsync(request.OperationId, "Request cluster stop", "/api/trace/cluster-stop", "/api/trace/cluster-stop/", request).ConfigureAwait(true);
    }

    /// <summary>確認した対象の抑止解除を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReleaseAsync()
    {
        if (state?.Closing.Version is not string version)
        {
            return;
        }

        TraceStopReleaseRequest? request = new TraceStopReleaseRequest(Guid.NewGuid(), version);
        await SendAsync(request.OperationId, "Release tracing restart hold", "/api/trace/cluster-stop/release", "/api/trace/operations/", request).ConfigureAwait(true);
    }

    /// <summary>操作を記録してから管理先へ送信し、現時点の結果を保持します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SendAsync<T>(Guid id, string kind, string path, string readPrefix, T request)
        where T : notnull
    {
        try
        {
            await Journal.StartAsync(id, kind, path, readPrefix + id, request, cancellationToken: UiCancellation).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            message = Ui.Deferred("Operation {0}: {1}. A stop request is not confirmation that all processes stopped.", id, OperationJournal.Outcome(Journal.LastResult));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or JSException)
        {
            message = Ui.Deferred("Operation {0} remains unconfirmed. Query it in operation history.", id);
        }
    }
}
