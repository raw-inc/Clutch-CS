namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>保持中の履歴の一ページと続き位置です。</summary>
/// <param name="Entries">小さい実行結果または省略を明示した要約。</param>
/// <param name="Cursor">次ページがない場合はnull。</param>
/// <param name="ObservationInstanceId">経過時間付き観測を返したCoreの実体です。従来の履歴照会ではnullです。</param>
/// <param name="AgeUnconfirmed">表示期間内か確認できない復元済み履歴がある場合はtrueです。</param>
public sealed record ExecutionHistoryPage(IReadOnlyList<ExecutionResultView> Entries, string? Cursor,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ObservationInstanceId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool AgeUnconfirmed = false);
