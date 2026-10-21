namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>アプリケーションを日時またはPC起動を基準に起動する予定です。</summary>
/// <param name="Id">編集しても同じ予定として追跡する識別子です。</param>
/// <param name="Mode">once、daily、weekdays、bootのいずれかです。</param>
/// <param name="TimeZoneId">実行日時を解釈するタイムゾーンです。</param>
/// <param name="At">一度だけ起動する日時です。</param>
/// <param name="Time">日次・曜日指定の時刻です。</param>
/// <param name="Weekdays">日曜0から土曜6の対象曜日です。</param>
/// <param name="AlreadyRunning">起動済みなら見送るか再起動するかです。</param>
/// <param name="StopPolicy">再起動で通常終了できなかった場合の明示方針です。</param>
/// <param name="StopTimeoutMilliseconds">再起動時の通常終了待ち上限です。</param>
/// <param name="Enabled">この予定を有効にするかです。</param>
/// <param name="BootDelayMilliseconds">PC起動から起動可能になるまでの経過時間です。</param>
/// <param name="SkipWhenLoggedOut">対話起動で未ログインの場合にその回を見送るかです。既定はログイン待機です。</param>
public sealed record ProcessSchedule(string Id, string Mode = "daily", string TimeZoneId = "UTC", string? At = null, string? Time = null,
    IReadOnlyList<int>? Weekdays = null, EProcessRunningPolicy AlreadyRunning = EProcessRunningPolicy.Skip,
    EProcessStopPolicy? StopPolicy = null, int StopTimeoutMilliseconds = 30000, bool Enabled = true,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int BootDelayMilliseconds = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool SkipWhenLoggedOut = false);
