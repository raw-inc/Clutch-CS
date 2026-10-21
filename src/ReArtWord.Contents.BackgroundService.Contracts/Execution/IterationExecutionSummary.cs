namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>未開始の要素を完了済みと取り違えない反復結果です。</summary>
/// <param name="Count">入力配列の要素数。</param>
/// <param name="Started">実行を開始した要素数。</param>
/// <param name="Items">保持できた要素の結果。常に入力順。</param>
public sealed record IterationExecutionSummary(int Count, int Started, IReadOnlyList<IterationExecutionResult> Items);
