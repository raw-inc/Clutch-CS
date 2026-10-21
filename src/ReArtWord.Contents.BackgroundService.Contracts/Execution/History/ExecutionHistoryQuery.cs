namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>新しい順の実行結果を件数ごとに分けて取得します。</summary>
/// <param name="Limit">一ページの最大件数。</param>
/// <param name="Cursor">直前ページが返した続き位置。</param>
/// <param name="MaximumBytes">応答JSONの最大バイト数。</param>
public sealed record ExecutionHistoryQuery(int Limit = 50, string? Cursor = null, int MaximumBytes = 524288);
