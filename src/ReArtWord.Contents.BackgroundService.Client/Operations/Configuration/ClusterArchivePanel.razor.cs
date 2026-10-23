using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>クラスターの設定とログを書き出す画面。</summary>
// 抑制理由: Razorの生成する公開partial型に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial型です。")]
public sealed partial class ClusterArchivePanel
{
    [Inject] private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject] private ConnectionState Connection
    {
        get; set;
    } = null!;

    [Inject] private IJSRuntime Js
    {
        get; set;
    } = null!;

    private bool includeLogs;
    private bool working;
    private Guid operation;
    private Func<string>? message;

    /// <summary>書き出しが完了したら、ブラウザーのダウンロードを開始します。</summary>
    /// <returns>書き出しの完了確認とダウンロード要求。</returns>
    // 抑制理由: UIイベントには引数を追加せず、画面を閉じた際のキャンセルをUiCancellationで各処理に伝えます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントからUiCancellationを引き継ぎます。")]
    private async Task ExportAsync()
    {
        working = true;
        message = null;
        string? cluster = Api.SelectedClusterId, peer = Api.SelectedPeerId;
        Guid? instance = Connection.Current?.InstanceId;
        try
        {
            if (cluster is null || peer is null || instance is null)
            {
                throw new InvalidOperationException("archive.targetUnavailable");
            }
            operation = Guid.NewGuid();
            ArchiveOperationResult result = await Api.PostForPeerAsync<ArchiveOperationResult>("/api/archive/export", new ArchiveExportRequest(operation, includeLogs), peer, cluster,
                cancellationToken: UiCancellation).ConfigureAwait(true) ?? throw new InvalidOperationException("archive.exportUnconfirmed");
            while (!result.Completed)
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                await Task.Delay(500, UiCancellation).ConfigureAwait(true);
                result = await Api.GetForPeerAsync<ArchiveOperationResult>("/api/archive/operations/" + operation.ToString("D"), peer, cluster, UiCancellation).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("archive.exportUnconfirmed");
            }
            if (result.OperationId != operation || result.Outcome != EOutcome.Succeeded || result.File is null)
            {
                throw new InvalidOperationException("archive.exportUnconfirmed");
            }
            string url = "/client/archives/" + operation.ToString("D") + "/download?cluster=" + Uri.EscapeDataString(cluster)
                + "&peer=" + Uri.EscapeDataString(peer) + "&instance=" + instance.Value.ToString("D");
            await Js.InvokeVoidAsync("contentsBrowser.downloadUrl", UiCancellation, url).ConfigureAwait(true);
            message = () => Ui["Archive download started."];
        }
        catch (OperationCanceledException) when (UiCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or JSException)
        {
            message = Ui.DeferredException(exception);
        }
        finally
        {
            working = false;
        }
    }
}
