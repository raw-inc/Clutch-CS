using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>接続先によらず、このブラウザーの日時表示を設定します。</summary>
// 抑制理由: Razorが生成する公開partial宣言と同じ型を構成します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言と同じ型を構成します。")]
public sealed partial class DisplaySettingsPanel
{
    private static readonly string[] zones = TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).Where(id => !string.Equals(id, "UTC", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    private string zoneId = "UTC";
    private string? message;
    private bool saving;

    [Inject]
    private DisplayTimePreferences Preferences
    {
        get; set;
    } = null!;

    /// <summary>現在の保存設定を編集欄へ設定します。</summary>
    protected override void OnInitialized() => zoneId = Preferences.Zone.Id;

    /// <summary>表示用設定だけを保存します。</summary>
    /// <returns>保存結果の画面反映です。</returns>
    // 抑制理由: UIイベントの固定署名で、画面寿命の取消しを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名でUiCancellationを使用します。")]
    private async Task SaveAsync()
    {
        saving = true;
        try
        {
            await Preferences.ChangeAsync(zoneId, UiCancellation).ConfigureAwait(true);
            message = "display.settings.saved";
        }
        catch (InvalidOperationException)
        {
            message = "display.timezone.invalid";
        }
        finally
        {
            saving = false;
        }
    }
}
