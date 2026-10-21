namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>一時追跡を解除して、確認した全参加実体へ停止を要求します。</summary>
/// <param name="OperationId">再送を通じて固定する操作。</param>
/// <param name="ExpectedTraceVersion">画面で確認した一時追跡の版。</param>
/// <param name="Targets">停止操作で確認した全参加実体。</param>
public sealed record TraceClusterStopRequest(Guid OperationId, string? ExpectedTraceVersion, IReadOnlyList<TraceStopTarget> Targets);
