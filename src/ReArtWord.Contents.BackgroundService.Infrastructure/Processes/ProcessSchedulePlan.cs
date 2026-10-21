using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>登録予定と日時Nodeで、同じタイムゾーン・暦計算を使用します。</summary>
internal static class ProcessSchedulePlan
{
    private static readonly string[] modes = ["once", "daily", "weekdays", "boot"];

    /// <summary>OSで未起動を確認済みのログイン待機だけを再開対象にします。</summary>
    internal static string AwaitingLogin => "process.scheduleAwaitingLogin";

    /// <summary>予定の指定値を検証し、共通の日時計算へ渡します。</summary>
    /// <returns>実行回の計算と、表示変更に影響されない時間指定の識別子です。</returns>
    internal static (SchedulePlan? Plan, string Timeline) Create(ProcessSchedule schedule, DateTimeOffset activatedAt)
    {
        if (string.IsNullOrWhiteSpace(schedule.Id) || 128 < schedule.Id.Length || !modes.Contains(schedule.Mode, StringComparer.Ordinal)
            || !Enum.IsDefined(schedule.AlreadyRunning) || schedule.BootDelayMilliseconds is < 0 or > 86400000
            || schedule.StopTimeoutMilliseconds is < 1 or > 3600000 || schedule.StopPolicy is { } policy && !Enum.IsDefined(policy)
            || schedule.AlreadyRunning == EProcessRunningPolicy.Restart && schedule.StopPolicy is null)
        {
            throw new FormatException("起動予定の種類、識別子、起動済み時の動作または停止方針が不正です。");
        }
        if (IsBoot(schedule))
        {
            return (null, ContractJson.Hash(new { schedule.Mode, schedule.BootDelayMilliseconds }));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.TimeZoneId);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        Dictionary<string, object?> settings = new(StringComparer.Ordinal) { ["mode"] = schedule.Mode };
        if (schedule.At is not null)
        {
            settings["at"] = schedule.At;
        }
        if (schedule.Time is not null)
        {
            settings["time"] = schedule.Time;
        }
        if (schedule.Weekdays is not null)
        {
            settings["weekdays"] = schedule.Weekdays;
        }
        JsonElement json = JsonSerializer.SerializeToElement(settings);
        return (new SchedulePlan(json, zone, activatedAt), ScheduleTimeline.Key(json, zone));
    }

    /// <summary>PC起動予定と暦の予定を区別します。</summary>
    /// <returns>PC起動を基準にするかです。</returns>
    internal static bool IsBoot(ProcessSchedule schedule) => string.Equals(schedule.Mode, "boot", StringComparison.Ordinal);

    /// <summary>表示日時は現在の壁時計へ写し、受付済み起動は再送候補にしません。</summary>
    /// <returns>次回候補と既知の状態です。</returns>
    internal static ProcessScheduleStatus BootStatus(ProcessSchedule schedule, ProcessScheduleCursor? cursor, SystemBootSession? boot, DateTimeOffset now)
    {
        if (!schedule.Enabled)
        {
            return new(schedule.Id, null, cursor?.ScheduledAt, cursor?.Outcome, cursor?.Reason);
        }
        if (boot is not { } observed)
        {
            return new(schedule.Id, now.AddSeconds(5), cursor?.ScheduledAt, Reason: "process.bootUnavailable");
        }
        if (cursor is not null && string.Equals(cursor.BootId, observed.Id, StringComparison.Ordinal) && !string.Equals(cursor.Reason, AwaitingLogin, StringComparison.Ordinal))
        {
            return new(schedule.Id, null, cursor.ScheduledAt, cursor.Outcome, cursor.Reason ?? "process.scheduleNextBoot");
        }
        DateTimeOffset due = now + TimeSpan.FromMilliseconds(schedule.BootDelayMilliseconds) - observed.Uptime;
        return new(schedule.Id, due, cursor?.ScheduledAt, cursor?.Outcome, cursor?.Reason);
    }
}
