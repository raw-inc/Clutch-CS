using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ReArtWord.Contents.BackgroundService.Client.DeviceManagement;

/// <summary>HostManagerの画面寿命と共通翻訳をブラウザー操作へ接続します。</summary>
// 抑制理由: Razorが生成する公開partial型を構成します。
[SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial型を構成します。")]
public sealed partial class HostManagerView
{
    [Inject]
    private IJSRuntime Script
    {
        get; set;
    } = null!;

    private ElementReference surface;
    private IJSObjectReference? module;
    private string? language;

    /// <summary>共通UiTextで翻訳したラベルだけを画面モジュールへ渡します。</summary>
    /// <returns>現在言語での初期表示です。</returns>
    // 抑制理由: Blazorの固定署名で画面寿命の取消をI/Oへ渡します。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを使用します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (string.Equals(language, Ui.Language, StringComparison.Ordinal))
        {
            return;
        }
        module ??= await Script.InvokeAsync<IJSObjectReference>("import", UiCancellation, "./host-manager/index.js").ConfigureAwait(true);
        string[] keys = await module.InvokeAsync<string[]>("textKeys", UiCancellation).ConfigureAwait(true);
        Dictionary<string, string> labels = keys.ToDictionary(key => key, key => Ui[key], StringComparer.Ordinal);
        await module.InvokeVoidAsync("mount", UiCancellation, surface, labels).ConfigureAwait(true);
        language = Ui.Language;
    }

    /// <summary>画面の通信と監視を閉じ、ClientHostの受付済み処理は維持します。</summary>
    /// <returns>画面の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            await module.InvokeVoidAsync("unmount", surface).ConfigureAwait(true);
            await module.DisposeAsync().ConfigureAwait(true);
        }
        await base.DisposeAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }
}
