namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>未指定項目はクライアントの既定表示を維持します。</summary>
public sealed record ClientAppearance(string? Title = null, string? Subtitle = null, string? IconPath = null, string? FaviconPath = null)
{
    /// <summary>表示設定を指定しない場合の共通タイトルです。</summary>
    public static string DefaultTitle => Management.ClientBranding.Default.ContentsName;

    /// <summary>表示設定を指定しない場合の共通サブタイトルです。</summary>
    public static string DefaultSubtitle => Management.ClientBranding.Default.Subtitle;
}
