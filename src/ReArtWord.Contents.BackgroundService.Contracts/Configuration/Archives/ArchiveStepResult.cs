using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>一括操作の一部成功を確認できる段階結果です。</summary>
/// <param name="Target">共通設定・WF・ログなどの対象です。</param>
/// <param name="Outcome">確認結果です。</param>
/// <param name="Reason">失敗や未確認の理由です。</param>
public sealed record ArchiveStepResult(string Target, EOutcome Outcome, string? Reason = null);
