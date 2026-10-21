namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>比較変更に使う保存版を伴う予約です。</summary>
/// <param name="Version">この照会で確認した保存版です。</param>
/// <param name="Schedule">固定入力と現在状態です。</param>
public sealed record UpdateScheduleView(string Version, ScheduledUpdate Schedule);
