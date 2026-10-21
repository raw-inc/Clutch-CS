using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>ダイアログの表示とフォーカスの復帰を管理します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class Modal : IAsyncDisposable
{
    private static string EscapeKey => "Escape";

    [Inject]
    private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    /// <summary>ダイアログを表示するかどうかです。</summary>
    [Parameter]
    public bool Open
    {
        get;
        set;
    }

    /// <summary>読み取り専用グラフなど横幅が必要な内容を表示します。</summary>
    [Parameter]
    public bool Wide
    {
        get; set;
    }

    /// <summary>画面に表示する見出しまたは翻訳キーです。</summary>
    [Parameter]
    public string Title
    {
        get;
        set;
    } = string.Empty;

    /// <summary>ダイアログ内に表示する内容です。</summary>
    [Parameter]
    public RenderFragment? ChildContent
    {
        get;
        set;
    }

    /// <summary>親画面へ表示終了を通知します。</summary>
    [Parameter]
    public EventCallback Close
    {
        get;
        set;
    }

    private readonly string focusId = Guid.NewGuid().ToString("N");
    private ElementReference dialog;
    private bool focused;

    /// <summary>表示開始時にダイアログへフォーカスし、終了時に元の要素へ戻します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && !focused)
        {
            focused = true;
            await Script.InvokeVoidAsync("contentsBrowser.openDialog", UiCancellation, dialog, focusId).ConfigureAwait(true);
        }
        else
        {
            if (!Open && focused)
            {
                focused = false;
                await Script.InvokeVoidAsync("contentsBrowser.closeDialog", UiCancellation, focusId).ConfigureAwait(true);
            }
        }
    }

    /// <summary>ダイアログのキーボード操作を親の終了通知へ変換します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task KeyAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, EscapeKey, StringComparison.Ordinal))
        {
            await Close.InvokeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>ダイアログ終了時に保持したフォーカスをブラウザーへ返します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!focused)
            {
                return;
            }

            try
            {
                await Script.InvokeVoidAsync("contentsBrowser.closeDialog", focusId).ConfigureAwait(true);
            }
            catch (JSException)
            {
            }
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
