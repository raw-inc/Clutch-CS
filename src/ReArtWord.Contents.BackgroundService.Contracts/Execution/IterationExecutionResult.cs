using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>入力順の添字と、容量を制限した要素ごとの実行結果です。</summary>
/// <param name="Index">元の配列のゼロ始まりの添字。</param>
/// <param name="Execution">要素の処理結果または詳細を省いた要約。</param>
public sealed record IterationExecutionResult(int Index, ExecutionResultView Execution);
