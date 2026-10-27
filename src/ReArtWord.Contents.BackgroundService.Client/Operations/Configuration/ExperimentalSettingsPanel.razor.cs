using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>試験的な機能の常時有効化を、このブラウザーに保存します。</summary>
// 抑制理由: Razorが生成する公開partial宣言と同じ型を構成します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言と同じ型を構成します。")]
public sealed partial class ExperimentalSettingsPanel
{
    private bool enabled;
    private bool busy = true;
    private string? message;

    [Inject]
    private BrowserStorage Storage
    {
        get; set;
    } = null!;

    /// <summary>保存済みの設定を表示します。</summary>
    /// <returns>設定の読取りと画面反映です。</returns>
    // 抑制理由: Blazorの固定署名で、画面寿命の取消しを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを使用します。")]
    protected override async Task OnInitializedAsync()
    {
        try
        {
            enabled = await Storage.ReadExperimentalPreferenceAsync(cancellationToken: UiCancellation).ConfigureAwait(true);
        }
        catch (JSException)
        {
            message = "experimental.settings.readFailed";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>保存に成功した場合だけ、保存済みと表示します。</summary>
    /// <returns>設定の保存と結果表示です。</returns>
    // 抑制理由: UIイベントの固定署名で、画面寿命の取消しを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名でUiCancellationを使用します。")]
    private async Task SaveAsync()
    {
        busy = true;
        try
        {
            await Storage.SaveExperimentalPreferenceAsync(enabled, UiCancellation).ConfigureAwait(true);
            message = "experimental.settings.saved";
        }
        catch (JSException)
        {
            message = "experimental.settings.writeFailed";
        }
        finally
        {
            busy = false;
        }
    }
}
