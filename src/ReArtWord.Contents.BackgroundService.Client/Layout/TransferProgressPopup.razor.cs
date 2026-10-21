using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Client.Layout;

/// <summary>画面を移動せず、開いている間だけ共有転送の確認済み進捗を表示します。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネント型です。")]
public sealed partial class TransferProgressPopup
{
    [Inject] private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>共通フッターが取得した進行中件数です。</summary>
    [Parameter] public int ActiveCount
    {
        get; set;
    }

    /// <summary>件数を再確認できていないことを進捗と区別します。</summary>
    [Parameter] public bool CountUnconfirmed
    {
        get; set;
    }

    private bool open, pinned, loading, unavailable;
    private SharedTransferQueueState? state;
    private Task reading = Task.CompletedTask;
    private static string EscapeKey => "Escape";
    private static string SucceededState => "Succeeded";

    /// <summary>列挙中は不定進捗とし、転送率から成功状態を推測しません。</summary>
    /// <returns>確認済みバイトの割合、または不定進捗です。</returns>
    internal static double? Percent(FileTransferJob job)
    {
        if (0 < job.TotalBytes)
        {
            return 100d * Math.Clamp(job.BytesCopied, 0, job.TotalBytes) / job.TotalBytes;
        }
        if (string.Equals(job.State, SucceededState, StringComparison.Ordinal))
        {
            return 100;
        }
        return null;
    }

    /// <summary>Windows・Unix両形式で末尾の表示名を取り出します。</summary>
    /// <returns>ファイルまたはフォルダーの名前です。</returns>
    internal static string FileName(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? path;

    /// <summary>ホバーで開き、確認済みの前回表示を保持したまま最新状態を読みます。</summary>
    /// <returns>今回の読み取りです。</returns>
    // 抑制理由: UIイベントの固定署名でUiCancellationを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task OpenAsync()
    {
        open = true;
        return RefreshAsync(UiCancellation);
    }

    /// <summary>クリックでは表示を固定し、再クリックで閉じます。</summary>
    /// <returns>表示する場合の読み取りです。</returns>
    // 抑制理由: UIイベントの固定署名でUiCancellationを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task ToggleAsync()
    {
        if (pinned)
        {
            Close();
            return Task.CompletedTask;
        }
        pinned = true;
        return OpenAsync();
    }

    /// <summary>ホバーだけで開いた表示を閉じます。</summary>
    private void Leave()
    {
        if (!pinned)
        {
            open = false;
        }
    }

    /// <summary>固定表示とホバー表示を終了します。</summary>
    private void Close() => open = pinned = false;

    /// <summary>Escでキーボードからも閉じられます。</summary>
    private void KeyDown(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            Close();
        }
    }

    /// <summary>レイアウトの定期取得を利用し、閉じている間や取得中は追加要求を送りません。</summary>
    /// <returns>実行中、または今回開始する一回の読み取りです。</returns>
    // 抑制理由: 読取りTaskを所有フィールドへ保持して返し、DisposeAsyncでも完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "readingへ保持して呼出元へ返し、DisposeAsyncでもawaitします。")]
    internal Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (ComponentDisposed || !open || !reading.IsCompleted)
        {
            return reading;
        }
        reading = ReadAsync(cancellationToken);
        return reading;
    }

    /// <summary>取得失敗は前回の値と区別し、成功した値を消しません。</summary>
    /// <returns>取得と、表示中の場合の再描画です。</returns>
    private async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        loading = true;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UiCancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            SharedTransferQueueState? observed = await Api.GetAsync<SharedTransferQueueState>("/client/transfers/progress", timeout.Token).ConfigureAwait(true);
            unavailable = observed is null;
            state = observed ?? state;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            unavailable = true;
        }
        finally
        {
            loading = false;
            if (!ComponentDisposed && open)
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
    }

    /// <summary>画面終了後に転送一覧の取得を残しません。</summary>
    /// <returns>所有する読取りの終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await reading.ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }
}
