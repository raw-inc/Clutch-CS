using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>選択されたタイムゾーンで実行回を計算する不変の指定です。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。"),
 SuppressMessage("SourceQuality", "RAW171", Justification = "選択されたタイムゾーンの曖昧・欠落時刻検証にTimeZoneInfoが要求するDateTimeを局所使用し、外部へはDateTimeOffsetのみ返します。")]
public sealed class SchedulePlan
{
    private const string SettingStart = "start";
    private const string SettingEnd = "end";
    private const string SettingTime = "time";
    private const string SettingHHMm = "HH:mm";
    private const string SettingHHMmSs = "HH:mm:ss";

    private const string IntervalMode = "interval";
    private const string OnceMode = "once";
    private const string WeekdaysMode = "weekdays";
    private readonly string mode;
    private readonly TimeZoneInfo timeZone;
    private readonly TimeSpan interval;
    private readonly DateTimeOffset anchor;
    private readonly DateTimeOffset? end;
    private readonly DateTimeOffset? validStart;
    private readonly TimeOnly time;
    private readonly HashSet<int> weekdays;
    private readonly HashSet<DateOnly> include;
    private readonly HashSet<DateOnly> exclude;

    /// <summary>一つの設定版を検証し実行回の計算へ使います。</summary>
    public SchedulePlan(JsonElement json, TimeZoneInfo timeZone, DateTimeOffset activatedAt)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        NodeSettings settings = new(json);
        this.timeZone = timeZone;
        mode = settings.ReadText("mode") ?? throw new FormatException("時間指定の種類が必要です。");
        interval = TimeSpan.FromMilliseconds(settings.ReadNumber("intervalMilliseconds", 1000));
        anchor = activatedAt;
        if (string.Equals(mode, IntervalMode, StringComparison.Ordinal))
        {
            anchor += interval;
        }
        if (settings.ReadText(SettingStart) is string start)
        {
            anchor = ParseInstant(start, timeZone);
            validStart = anchor;
        }
        if (settings.ReadText(SettingEnd) is string endText)
        {
            end = ParseInstant(endText, timeZone);
            if (validStart is not null && end < validStart)
            {
                throw new FormatException("終了が開始より前です。");
            }
        }
        if (string.Equals(mode, OnceMode, StringComparison.Ordinal))
        {
            anchor = ParseInstant(settings.ReadText("at") ?? throw new FormatException("実行日時が必要です。"), timeZone);
        }
        if (!string.Equals(mode, IntervalMode, StringComparison.Ordinal) && !string.Equals(mode, OnceMode, StringComparison.Ordinal) && !TimeOnly.TryParseExact(settings.ReadText(SettingTime), [SettingHHMm, SettingHHMmSs], CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            throw new FormatException("実行時刻が必要です。");
        }
        weekdays = [];
        JsonElement days = settings.Get("weekdays");
        if (string.Equals(mode, WeekdaysMode, StringComparison.Ordinal) && days.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement day in days.EnumerateArray())
            {
                int number = day.GetInt32();
                if (number < 0 || 6 < number)
                {
                    throw new FormatException("曜日は日曜0から土曜6です。");
                }
                weekdays.Add(number);
            }
        }
        if (string.Equals(mode, WeekdaysMode, StringComparison.Ordinal) && weekdays.Count == 0)
        {
            throw new FormatException("曜日を一つ以上選択してください。");
        }
        include = ReadDates(settings.Get("includeDates"));
        exclude = ReadDates(settings.Get("excludeDates"));
    }

    /// <summary>基準後の次回を求めます。</summary>
    /// <returns>実行回が残っていればその時刻です。</returns>
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        if (string.Equals(mode, OnceMode, StringComparison.Ordinal))
        {
            if (after < anchor && Eligible(anchor))
            {
                return anchor;
            }
            return null;
        }
        if (string.Equals(mode, IntervalMode, StringComparison.Ordinal))
        {
            long steps = 0;
            if (anchor <= after)
            {
                steps = ((after - anchor).Ticks / interval.Ticks) + 1;
            }
            DateTimeOffset next = anchor.AddTicks(checked(steps * interval.Ticks));
            while (!Eligible(next))
            {
                if (end is not null && end < next)
                {
                    return null;
                }

                // 除外日は時刻回を一つずつ走査せず、翌日の最初の回へ移動します。
                DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, timeZone).DateTime).AddDays(1);
                DateTimeOffset midnight = AtDate(date, TimeOnly.MinValue);
                steps = Math.Max(0, (midnight - anchor).Ticks / interval.Ticks);
                next = anchor.AddTicks(checked(steps * interval.Ticks));
                if (next < midnight)
                {
                    next += interval;
                }
            }
            return next;
        }
        if (after < anchor)
        {
            after = anchor.AddTicks(-1);
        }
        DateOnly candidate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, timeZone).DateTime);
        while (candidate < DateOnly.MaxValue)
        {
            DateTimeOffset instant = AtDate(candidate, time);
            if (end is not null && end < instant)
            {
                return null;
            }
            bool weekdayAllowed = !string.Equals(mode, WeekdaysMode, StringComparison.Ordinal) || weekdays.Contains((int)candidate.DayOfWeek);
            if (after < instant && (weekdayAllowed || include.Contains(candidate)) && Eligible(instant))
            {
                return instant;
            }
            candidate = candidate.AddDays(1);
        }
        return null;
    }

    /// <summary>未実行回が累積した場合に最新の実行可能回だけを選びます。</summary>
    /// <returns>未来の次回または現在までの最新回です。</returns>
    public DateTimeOffset Latest(DateTimeOffset earliest, DateTimeOffset now)
    {
        if (string.Equals(mode, IntervalMode, StringComparison.Ordinal))
        {
            DateTimeOffset limit = now;
            if (end is not null && end < limit)
            {
                limit = end.Value;
            }
            if (limit < earliest)
            {
                return earliest;
            }
            long steps = (limit - earliest).Ticks / interval.Ticks;
            DateTimeOffset result = earliest.AddTicks(checked(steps * interval.Ticks));
            while (!Eligible(result) && earliest < result)
            {
                DateOnly previous = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(result, timeZone).DateTime);
                DateTimeOffset boundary = AtDate(previous, TimeOnly.MinValue).AddTicks(-1);
                steps = (boundary - earliest).Ticks / interval.Ticks;
                result = earliest.AddTicks(checked(steps * interval.Ticks));
            }
            return result;
        }
        DateTimeOffset latest = earliest;
        DateTimeOffset? next = Next(latest);
        while (next is not null && next <= now)
        {
            latest = next.Value;
            next = Next(latest);
        }
        return latest;
    }

    /// <summary>有効期間と日付除外を評価します。</summary>
    /// <returns>この回を使用できるかを示します。</returns>
    private bool Eligible(DateTimeOffset instant)
    {
        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
        DateTimeOffset minimum = anchor;
        if (string.Equals(mode, OnceMode, StringComparison.Ordinal))
        {
            minimum = validStart ?? DateTimeOffset.MinValue;
        }
        return minimum <= instant && (end is null || instant <= end) && !exclude.Contains(day);
    }

    /// <summary>共通タイムゾーンにおける日付時刻を変換します。</summary>
    /// <returns>UTCとのずれを含む時刻です。</returns>
    private DateTimeOffset AtDate(DateOnly date, TimeOnly at)
    {
        DateTime local = date.ToDateTime(at, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local))
        {
            throw new FormatException("夏時間の重複・欠落時刻は保証対象外です。");
        }
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    /// <summary>日時を明示オフセットまたは共通タイムゾーンで解釈します。</summary>
    /// <returns>解釈した実行日時です。</returns>
    public static DateTimeOffset ParseInstant(string text, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(zone);
        bool offset = ScheduleTimeline.HasOffset(text);
        if (offset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset instant))
        {
            return instant;
        }
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            throw new FormatException("日時が不正です。");
        }
        DateTime local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new FormatException("夏時間の重複・欠落日時は保証対象外です。");
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>例外日を文化非依存で読み取ります。</summary>
    /// <returns>日付の集合です。</returns>
    private static HashSet<DateOnly> ReadDates(JsonElement values)
    {
        HashSet<DateOnly> dates = [];
        if (values.ValueKind == JsonValueKind.Undefined)
        {
            return dates;
        }
        foreach (JsonElement item in values.EnumerateArray())
        {
            dates.Add(DateOnly.ParseExact(item.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return dates;
    }
}
