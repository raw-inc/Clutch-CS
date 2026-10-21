using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration;

/// <summary>保存・共有・スナップショット・有効化の一段階の実行結果です。</summary>
/// <param name="Outcome">確認結果。</param>
/// <param name="Reason">非成功理由。</param>
public sealed record ConfigurationPhaseResult(EOutcome Outcome, string? Reason = null);
