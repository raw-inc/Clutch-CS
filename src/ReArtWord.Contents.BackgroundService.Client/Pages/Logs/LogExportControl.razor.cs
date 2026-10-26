using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Client.Pages.Logs;

/// <summary>検索条件を一回の出力へ固定し、本文を取得せず進捗だけを追跡します。</summary>
// 抑制理由: Razorが生成する公開型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開partial宣言に合わせます。")]
public sealed partial class LogExportControl : IAsyncDisposable
{
    [Inject] private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject] private IJSRuntime Js
    {
        get; set;
    } = null!;

    /// <summary>クリックした時点のフォームを検証して読み取ります。</summary>
    [Parameter, EditorRequired] public Func<LogQuery> Query
    {
        get; set;
    } = null!;

    /// <summary>全観測先を対象にするかです。</summary>
    [Parameter] public bool AllClusters
    {
        get; set;
    }

    /// <summary>選択範囲のクラスタです。</summary>
    [Parameter] public string? ClusterId
    {
        get; set;
    }

    /// <summary>選択範囲のPeerです。</summary>
    [Parameter] public string? PeerId
    {
        get; set;
    }

    /// <summary>検索中に重複操作しないための画面状態です。</summary>
    [Parameter] public bool Disabled
    {
        get; set;
    }

    private readonly CancellationTokenSource lifetime = new();
    private Task active = Task.CompletedTask;
    private LogExportStatus? status;
    private Func<string>? problem;
    private bool working, disposed;
    private string DownloadUrl => "/client/log-exports/" + status!.Id.ToString("D") + "/download";

    /// <summary>進捗照会が必要な受付・実行状態だけを選びます。</summary>
    /// <returns>生成が完了していないかです。</returns>
    private static bool IsRunning(LogExportStatus? value) => value?.State is "queued" or "running";

    /// <summary>所有する非同期処理を登録して開始します。</summary>
    /// <returns>ファイル生成とダウンロード開始の完了です。</returns>
    // 抑制理由: UI callbackは画面寿命のトークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI callbackから所有トークンを渡します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "activeへ所有し、イベント呼出元とDisposeAsyncで完了を待ちます。")]
    private Task StartAsync(ELogExportFormat format)
    {
        if (!working)
        {
            active = GenerateAsync(format, lifetime.Token);
        }
        return active;
    }

    /// <summary>受付直後に標準ダウンロードを開始し、本文を保持せず進捗だけを取得します。</summary>
    /// <returns>一回の出力結果です。</returns>
    private async Task GenerateAsync(ELogExportFormat format, CancellationToken cancellationToken = default)
    {
        working = true;
        problem = null;
        status = null;
        try
        {
            LogQuery filter = Query();
            LogSearchSource? source = null;
            if (!AllClusters)
            {
                source = new(ClusterId ?? throw new InvalidOperationException(), PeerId ?? throw new InvalidOperationException());
            }
            status = await Api.PostAsync<LogExportStatus>("/client/log-exports/", new LogExportRequest(filter, DisplayTimeZone.Id, source) { Format = format }, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("log.export.failed");
            await Js.InvokeVoidAsync("contentsBrowser.downloadUrl", cancellationToken, DownloadUrl).ConfigureAwait(true);
            while (IsRunning(status))
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
                status = await Api.GetAsync<LogExportStatus>("/client/log-exports/" + status.Id.ToString("D"), cancellationToken).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("log.export.failed");
            }
            string completed = "completed";
            if (!string.Equals(status.State, completed, StringComparison.Ordinal))
            {
                problem = () => Ui[status.Problem ?? "log.export.failed"];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or FormatException or System.Text.Json.JsonException or JSException)
        {
            problem = Ui.DeferredException(exception);
        }
        finally
        {
            await CancelUnfinishedAsync().ConfigureAwait(true);
            working = false;
            if (!disposed)
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
    }

    /// <summary>現在の一時出力だけを取り消します。</summary>
    /// <returns>取消要求の受付です。</returns>
    // 抑制理由: UI callbackから所有トークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI callbackは画面寿命で取り消します。")]
    private async Task CancelAsync()
    {
        try
        {
            if (status is not null)
            {
                await Api.PostAsync<object>("/client/log-exports/" + status.Id.ToString("D") + "/cancel", new { }, lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        {
            problem = Ui.DeferredException(exception);
        }
    }

    /// <summary>画面を離れた場合は未完了の生成も取り消します。</summary>
    /// <returns>所有する進捗照会と取消の終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "所有する処理を有限期限で回収します。")]
    public override async ValueTask DisposeAsync()
    {
        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(true);
        await active.ConfigureAwait(true);
        lifetime.Dispose();
        await base.DisposeAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>画面離脱や進捗照会の失敗後も、送信が残れば有限期限で停止させます。</summary>
    /// <returns>取消受付または期限までの待機です。</returns>
    // 抑制理由: 寿命トークンの取消後も解放要求を送るため、独立した有限期限を所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "停止後の解放要求に専用の5秒期限を使用します。")]
    private async Task CancelUnfinishedAsync()
    {
        try
        {
            if (IsRunning(status))
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await Api.PostAsync<object>("/client/log-exports/" + status!.Id.ToString("D") + "/cancel", new { }, timeout.Token).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // ClientHost側も切断と有限の送信期限で回収します。
        }
    }
}
