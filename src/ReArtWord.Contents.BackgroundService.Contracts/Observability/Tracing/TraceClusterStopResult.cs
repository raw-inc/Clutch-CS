using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>一時追跡解除と停止要求の対象別結果を分けて返します。</summary>
/// <param name="OperationId">操作識別子。</param>
/// <param name="Outcome">解除と停止要求受付の全体結果。</param>
/// <param name="TraceClear">一時追跡解除の共有確認。</param>
/// <param name="Targets">元の実体ごとの受付確認。</param>
/// <param name="Reason">結果説明。実全停止は別の運用確認を要します。</param>
public sealed record TraceClusterStopResult(Guid OperationId, EOutcome Outcome, EOutcome TraceClear, IReadOnlyList<TraceStopTargetResult> Targets, string? Reason = null);
