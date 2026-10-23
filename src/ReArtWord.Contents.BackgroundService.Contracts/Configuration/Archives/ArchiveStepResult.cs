using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>復元の各工程の結果。</summary>
/// <param name="Target">処理工程（設定保存、反映確認、ログ取り込み）。</param>
/// <param name="Outcome">確認結果です。</param>
/// <param name="Reason">失敗や未確認の理由です。</param>
public sealed record ArchiveStepResult(string Target, EOutcome Outcome, string? Reason = null);
