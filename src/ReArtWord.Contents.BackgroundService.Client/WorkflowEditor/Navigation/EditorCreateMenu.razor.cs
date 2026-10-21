using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>追加ボタンの右クリックで新規文書の種類を選択します。</summary>
// 抑制理由: Razorが生成する公開partialと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class EditorCreateMenu
{
    /// <summary>通常のWFを作成します。</summary>
    [Parameter]
    public EventCallback New
    {
        get; set;
    }

    /// <summary>WFDashboardを作成します。</summary>
    [Parameter]
    public EventCallback NewDashboard
    {
        get; set;
    }

    /// <summary>追加の作成候補を公開するかです。</summary>
    [Parameter]
    public bool Experimental
    {
        get; set;
    }

    /// <summary>文書操作中は作成を開始しません。</summary>
    [Parameter]
    public bool Busy
    {
        get; set;
    }

    private bool open, focus;
    private double x, y;
    private ElementReference menu;
    private string MenuStyle => FormattableString.Invariant($"left:clamp(8px,{x}px,calc(100vw - 270px));top:clamp(8px,{y}px,calc(100vh - 100px))");

    /// <summary>作成できなくなったときは開いていた選択肢も閉じます。</summary>
    protected override void OnParametersSet()
    {
        if (!Experimental || Busy)
        {
            open = focus = false;
        }
    }

    /// <summary>通常のクリック動作を維持し、実験時だけ種類を選択します。</summary>
    private void Show(MouseEventArgs args)
    {
        if (Experimental && !Busy)
        {
            x = args.ClientX;
            y = args.ClientY;
            open = focus = true;
        }
    }

    /// <summary>Escapeで作成メニューだけを閉じます。</summary>
    private void Key(KeyboardEventArgs args)
    {
        string escape = "Escape";
        if (string.Equals(args.Key, escape, StringComparison.Ordinal))
        {
            open = false;
        }
    }

    /// <summary>メニューからのキー操作を受け付けます。</summary>
    /// <returns>フォーカス設定です。</returns>
    // 抑制理由: Blazor固定署名で、要素のフォーカス以外のI/Oを持ちません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focus)
        {
            focus = false;
            await menu.FocusAsync().ConfigureAwait(true);
        }
    }

    /// <summary>種類を変更せず、独立した文書の作成へ進みます。</summary>
    /// <returns>親での草稿保持と作成です。</returns>
    private Task CreateAsync(bool dashboard, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        open = false;
        if (Busy || (dashboard && !Experimental))
        {
            return Task.CompletedTask;
        }
        if (dashboard)
        {
            return NewDashboard.InvokeAsync();
        }
        return New.InvokeAsync();
    }
}
