namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>接続先Coreに保存し、次回の通常セッションから適用する起動方針です。</summary>
/// <param name="StrictUserToken">OSの制限トークンとMedium整合性で通常起動を保証するかです。既定は従来方式です。</param>
/// <param name="Version">読取りで取得した保存版です。変更時の競合検出に使います。</param>
public sealed record TerminalSettings(bool StrictUserToken = false, string? Version = null);
