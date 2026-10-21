using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>日時Nodeと同じ暦規則で、電源予定を計算します。</summary>
internal static class HostPowerSchedulePlan
{
    /// <summary>不明な操作・曜日・timezoneを、保存と実行の両方で拒否します。</summary>
    /// <returns>検証済みの不変な暦指定です。</returns>
    internal static SchedulePlan Create(HostPowerSchedule schedule, DateTimeOffset activatedAt)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Id == Guid.Empty || !Enum.IsDefined(schedule.Action) || schedule.Weekdays?.Any(day => day is < 0 or > 6) == true
            || schedule.Weekdays?.Distinct().Count() != schedule.Weekdays?.Count)
        {
            throw new ArgumentException("powerSchedule.invalid", nameof(schedule));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.TimeZoneId);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        string mode = schedule.Weekdays is { Count: > 0 } ? "weekdays"
                                                          : "daily";
        return new(JsonSerializer.SerializeToElement(new { mode, time = schedule.Time, weekdays = schedule.Weekdays }), zone, activatedAt);
    }

    /// <summary>停止中や長い観測中断を遡らず、今回の短い観測区間に到来した一回だけを返します。</summary>
    /// <returns>今回処理する予定時刻です。</returns>
    internal static DateTimeOffset? Due(HostPowerSchedule schedule, DateTimeOffset changedAt, DateTimeOffset after, DateTimeOffset now)
        => Due(Create(schedule, changedAt), schedule.Enabled, changedAt, after, now);

    /// <summary>監視では一度解析した暦を再利用し、毎秒のJSON生成やtimezone解決を省きます。</summary>
    /// <returns>今回の観測区間へ到来した一回です。</returns>
    internal static DateTimeOffset? Due(SchedulePlan plan, bool enabled, DateTimeOffset changedAt, DateTimeOffset after, DateTimeOffset now)
    {
        if (!enabled || now <= after || TimeSpan.FromMinutes(1) < now - after)
        {
            return null;
        }
        DateTimeOffset? due = plan.Next(after);
        return due is not null && changedAt <= due && due <= now ? due
                                                                 : null;
    }
}
