namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>Clutch-CSのClientHostが配信するヘッダーとタブアイコンの表示設定です。</summary>
/// <param name="ContentsName">ヘッダー先頭の表示文字列です。既定はClutch-CSです。</param>
/// <param name="HeaderLogo">ヘッダーロゴの配信URLです。</param>
/// <param name="ContentsLogo">先頭文字列に代えて表示する画像のURLです。</param>
/// <param name="Favicon">タブアイコンの画像です。設定で省略した場合はHeaderLogo、画像未設定なら同梱アイコンを使います。</param>
/// <param name="Subtitle">ヘッダー下段の表示文字列です。既定はWebUIです。</param>
public sealed record ClientBranding(string ContentsName = "Clutch-CS", string? HeaderLogo = null, string? ContentsLogo = null, string? Favicon = null, string Subtitle = "WebUI")
{
    /// <summary>導入画面と配信Hostで共用する表示の既定値です。</summary>
    public static ClientBranding Default
    {
        get;
    } = new();

    /// <summary>配信できる表示画像の最大サイズです。</summary>
    public static long MaximumImageBytes => 2 * 1024 * 1024;

    /// <summary>タイトルとサブタイトルの最大文字数です。</summary>
    public static int MaximumTextLength => 80;
}
