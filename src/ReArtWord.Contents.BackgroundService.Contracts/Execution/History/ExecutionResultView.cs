namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>小さい実行結果と、大きい結果の要約を明確に区別します。</summary>
/// <param name="Report">完全な結果。省略時はnull。</param>
/// <param name="Summary">省略時にも保持する確定結果。</param>
/// <param name="DetailReason">詳細を含めない理由。省略がなければnull。</param>
public sealed record ExecutionResultView(ExecutionReport? Report, ExecutionReportSummary? Summary = null, string? DetailReason = null);
