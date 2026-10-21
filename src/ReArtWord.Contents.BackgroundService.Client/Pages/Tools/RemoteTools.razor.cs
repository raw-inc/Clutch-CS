using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ReArtWord.Contents.BackgroundService.Client.Pages.Tools;

/// <summary>対話端末とファイルツリーの画面寿命をJavaScriptへ渡します。</summary>
// 抑制理由: Razorが生成する公開partial型を構成します。
[SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial型を構成します。")]
public sealed partial class RemoteTools : IAsyncDisposable
{
    [Inject]
    private IJSRuntime Script
    {
        get; set;
    } = null!;

    /// <summary>端末またはファイル画面の表示種別です。</summary>
    [Parameter]
    public string Mode
    {
        get; set;
    } = "terminal";

    private ElementReference surface;
    private IJSObjectReference? module;
    private string? mounted;
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>経路が変わった場合は前の画面監視を解放して表示を切り替えます。</summary>
    /// <returns>画面の初期化です。</returns>
    // 抑制理由: Blazor固定署名で、I/Oへ画面のlifetimeを渡します。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "Blazor固定署名で、I/Oへ画面のlifetimeを渡します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        string timeZone = DisplayTimeZone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone, out string? iana))
        {
            timeZone = iana;
        }
        string version = Mode + Ui.Language + timeZone;
        if (string.Equals(mounted, version, StringComparison.Ordinal))
        {
            return;
        }
        module ??= await Script.InvokeAsync<IJSObjectReference>("import", lifetime.Token, "./remote-tools.js").ConfigureAwait(true);
        await module.InvokeVoidAsync("mount", lifetime.Token, surface, Mode, Ui.Language, timeZone).ConfigureAwait(true);
        mounted = version;
    }

    /// <summary>画面側のイベント・通信だけを終了し、ピアの端末と転送は維持します。</summary>
    /// <returns>画面資源の解放です。</returns>
    // 抑制理由: IAsyncDisposable固定署名です。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposable固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(true);
        if (module is not null)
        {
            await module.InvokeVoidAsync("unmount", surface).ConfigureAwait(true);
            await module.DisposeAsync().ConfigureAwait(true);
        }
        lifetime.Dispose();
        await base.DisposeAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }
}
