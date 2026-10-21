using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>ブラウザーで使用する表示言語を選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class LanguageSelector
{
    private readonly string menuId = "language-menu-" + Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim rendering = new(1, 1);
    private ElementReference root;
    private IJSObjectReference? module, menu;
    private static string Japanese => "ja";
    private string LanguageName => string.Equals(Ui.Language, Japanese, StringComparison.Ordinal) ? "日本語"
                                                                                                  : "English";

    [Inject]
    private IJSRuntime Script
    {
        get; set;
    } = null!;

    /// <summary>メニューのポインター・キーボード操作をブラウザーへ接続します。</summary>
    /// <returns>イベント登録の完了です。</returns>
    // 抑制理由: Blazorの固定署名で、画面の所有トークンを利用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名で、画面の所有トークンを利用します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }
        await rendering.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            if (!ComponentDisposed)
            {
                module = await Script.InvokeAsync<IJSObjectReference>("import", UiCancellation, "./language-menu.js").ConfigureAwait(true);
                menu = await module.InvokeAsync<IJSObjectReference>("attachLanguageMenu", UiCancellation, root).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (ComponentDisposed)
        {
        }
        finally
        {
            rendering.Release();
        }
    }

    /// <summary>選択した表示言語を、画面の存続期間に対応する取消し付きで保存します。</summary>
    /// <returns>表示言語の変更と保存の完了です。</returns>
    // 抑制理由: Blazorの変更イベント署名を維持し、所有トークンを保存処理へ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの変更イベント署名で、保存処理にはUiCancellationを明示します。")]
    private Task ChangeLanguageAsync(string language) => Ui.ChangeAsync(language, UiCancellation);

    /// <summary>メニューが登録したイベントとJavaScript参照を解放します。</summary>
    /// <returns>画面資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await rendering.WaitAsync().ConfigureAwait(true);
        try
        {
            if (menu is not null)
            {
                await menu.InvokeVoidAsync("dispose").ConfigureAwait(true);
                await menu.DisposeAsync().ConfigureAwait(true);
            }
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            rendering.Release();
            rendering.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
