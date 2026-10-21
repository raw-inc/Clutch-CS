using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>停止要求の受付確認です。実プロセス終了の確認ではありません。</summary>
/// <param name="Target">元の対象実体。</param>
/// <param name="Acceptance">停止要求の永続受付結果。</param>
/// <param name="Reason">非成功理由。</param>
public sealed record TraceStopTargetResult(TraceStopTarget Target, EOutcome Acceptance, string? Reason = null);
