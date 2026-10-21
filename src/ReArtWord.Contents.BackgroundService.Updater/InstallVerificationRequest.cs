using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.Updater;

/// <summary>OS登録前後に同じ初回配置を検証するローカル入力です。</summary>
/// <param name="ConfigurationPath">外部TOML絶対パスです。</param>
/// <param name="ManifestPath">検証対象マニフェスト絶対パスです。</param>
/// <param name="CheckReady">falseは登録前検査、trueは通常参加確認です。</param>
/// <param name="TimeoutMilliseconds">通常参加確認の有限期限です。</param>
/// <param name="ConfigurationHash">登録前に確認したTOMLのハッシュです。</param>
/// <param name="ManifestHash">登録前に確認したマニフェストのハッシュです。</param>
/// <param name="StartedAfter">この配置の登録前時刻です。</param>
// 抑制理由: JSON入力境界でSystem.Text.Jsonがコンストラクターを呼び出すDTOです。
[SuppressMessage("Performance", "CA1812", Justification = "standalone installerのJSON入力から生成されます。")]
internal sealed record InstallVerificationRequest(string ConfigurationPath, string ManifestPath, bool CheckReady = false, int TimeoutMilliseconds = 60000, string? ConfigurationHash = null, string? ManifestHash = null, DateTimeOffset? StartedAfter = null);
