using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>一予定につき直近一件だけを保持し、同じ予定時刻の起動を再送しません。</summary>
/// <param name="Timeline">時間指定の識別子です。</param>
/// <param name="After">この時刻までの予定を受付済みとします。</param>
/// <param name="ScheduledAt">最後に受け付けた予定時刻です。</param>
/// <param name="Outcome">最後の操作の成否です。未確定の起動を自動再実行しません。</param>
/// <param name="Reason">最後の処理結果です。</param>
/// <param name="BootId">最後に受付済み、または新規登録時に見送ったOS起動回です。</param>
internal sealed record ProcessScheduleCursor(string Timeline, DateTimeOffset After, DateTimeOffset? ScheduledAt = null, EOutcome? Outcome = null, string? Reason = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? BootId = null);
