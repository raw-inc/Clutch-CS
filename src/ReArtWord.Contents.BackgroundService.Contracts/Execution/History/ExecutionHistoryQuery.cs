namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>新しい順の実行結果を件数ごとに分けて取得します。</summary>
/// <param name="Limit">一ページの最大件数。</param>
/// <param name="Cursor">直前ページが返した続き位置。</param>
/// <param name="MaximumBytes">応答JSONの最大バイト数。</param>
/// <param name="SummariesOnly">経過時間付きの観測では、trueなら60秒の活動要約、falseなら5分のノード診断を取得します。</param>
public sealed record ExecutionHistoryQuery(int Limit = 50, string? Cursor = null, int MaximumBytes = 524288,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool SummariesOnly = false);
