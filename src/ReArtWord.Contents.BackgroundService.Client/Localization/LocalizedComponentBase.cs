using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.Localization;

/// <summary>子画面に表示言語と、画面の存続期間に対応する取消しを提供します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントの基底型です。
[SuppressMessage("Design", "CA1515", Justification = "生成される公開Razor componentの基底型です。")]
public abstract class LocalizedComponentBase : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? componentLifetime;
    private bool componentDisposed;
    private Task rendering = Task.CompletedTask;

    /// <summary>非同期I/Oを行う画面だけが所有し、破棄後は取消済みとなるトークンです。</summary>
    protected CancellationToken UiCancellation
    {
        get
        {
            if (componentDisposed)
            {
                return new CancellationToken(true);
            }
            componentLifetime ??= new();
            return componentLifetime.Token;
        }
    }

    /// <summary>破棄後の通知やDOM操作を開始しないための状態です。</summary>
    protected bool ComponentDisposed => componentDisposed;

    /// <summary>同期イベントからの再描画を一つにまとめ、終了時に確認するTaskとして保持します。</summary>
    // 抑制理由: 描画Taskはフィールドで保持し、DisposeAsyncで完了を確認します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "イベントからの再描画を一つの所有Taskへまとめ、終了時にawaitします。")]
    protected void RequestRenderFromEvent()
    {
        if (!componentDisposed && rendering.IsCompletedSuccessfully)
        {
            rendering = RenderFromEventAsync(UiCancellation);
        }
    }

    /// <summary>イベント起点の描画をDispatcherへ渡し、失敗はBlazorの例外境界へ通知します。</summary>
    /// <returns>描画要求または例外通知の完了です。</returns>
    // 抑制理由: イベント起点の失敗を未観測Taskにせず、Blazorの例外境界へ渡します。
    [SuppressMessage("Design", "CA1031", Justification = "UIイベントの例外境界として失敗をDispatchExceptionAsyncへ通知します。")]
    private async Task RenderFromEventAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (!componentDisposed && !cancellationToken.IsCancellationRequested)
                {
                    StateHasChanged();
                }
            }).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (!componentDisposed)
            {
                await DispatchExceptionAsync(exception).ConfigureAwait(true);
            }
        }
    }

    /// <summary>ブラウザーごとの翻訳サービスです。</summary>
    [Inject]
    protected UiText Ui
    {
        get; set;
    } = null!;

    /// <summary>親レイアウトの言語変更を子コンポーネントへ伝えます。</summary>
    [CascadingParameter(Name = "DisplayLanguage")]
    public string DisplayLanguage
    {
        get; set;
    } = "en";

    /// <summary>保存された表示用タイムゾーンの変更を子画面へ伝えます。</summary>
    [CascadingParameter(Name = "DisplayTimeZone")]
    public TimeZoneInfo DisplayTimeZone
    {
        get; set;
    } = TimeZoneInfo.Utc;

    /// <summary>保存元の日時を変更せず、選択したタイムゾーンで表示します。</summary>
    /// <returns>表示用日時、または未観測を表すnullです。</returns>
    protected string? FormatTime(DateTimeOffset? value, string format = "yyyy-MM-dd HH:mm:ss.fff zzz")
        => value is { } instant ? TimeZoneInfo.ConvertTime(instant, DisplayTimeZone).ToString(format, System.Globalization.CultureInfo.CurrentCulture)
                                : null;

    /// <summary>画面が開始した非同期I/Oを取り消し、必要時にだけ作成した取消資源を解放します。</summary>
    /// <returns>取消通知と資源解放の完了です。</returns>
    // 抑制理由: IAsyncDisposable の固定署名で、画面が所有する取消資源を終了します。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposable の固定署名であり、所有トークン自体を取り消す終了境界です。")]
    public virtual async ValueTask DisposeAsync()
    {
        if (componentDisposed)
        {
            return;
        }
        componentDisposed = true;
        try
        {
            if (componentLifetime is not null)
            {
                await componentLifetime.CancelAsync().ConfigureAwait(true);
            }
            await rendering.ConfigureAwait(true);
        }
        finally
        {
            componentLifetime?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
