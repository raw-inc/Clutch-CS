using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>Webクライアントの表示設定とプレビュー用の文字列を管理します。</summary>
internal sealed partial class AppearanceViewModel : ObservableObject
{
    /// <summary>空欄の場合は既定のタイトルを使います。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewTitle))]
    public partial string Title
    {
        get; set;
    } = string.Empty;

    /// <summary>空欄の場合は既定のサブタイトルを使います。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewSubtitle))]
    public partial string Subtitle
    {
        get; set;
    } = string.Empty;

    /// <summary>画面のアイコンに使う画像ファイルです。</summary>
    [ObservableProperty]
    public partial string Icon
    {
        get; set;
    } = string.Empty;

    /// <summary>ブラウザーのタブに使う画像ファイルです。</summary>
    [ObservableProperty]
    public partial string Favicon
    {
        get; set;
    } = string.Empty;

    /// <summary>空欄時の既定値を含めたタイトルです。</summary>
    public string PreviewTitle => Optional(Title) ?? ClientAppearance.DefaultTitle;

    /// <summary>空欄時の既定値を含めたサブタイトルです。</summary>
    public string PreviewSubtitle => Optional(Subtitle) ?? ClientAppearance.DefaultSubtitle;

    /// <summary>入力された項目だけを導入設定に反映します。</summary>
    /// <returns>既定値の選択を保持した表示設定です。</returns>
    public ClientAppearance Capture() => new(Optional(Title), Optional(Subtitle), Optional(Icon), Optional(Favicon));

    /// <summary>空欄を未指定として扱います。</summary>
    /// <returns>前後の空白を除いた値、またはnullです。</returns>
    private static string? Optional(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return value.Trim();
    }
}
