using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>表示や遅延方針の編集で、同じ時間指定の基準を作り直さないための識別です。</summary>
public static class ScheduleTimeline
{
    /// <summary>予定時刻を決める設定だけを、順序に依存しないキーへ変換します。</summary>
    /// <returns>同じ時間指定を表す識別子。</returns>
    public static string Key(JsonElement settings, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        NodeSettings values = new(settings);
        string mode = values.ReadText("mode")!;
        Dictionary<string, object?> timing = new(StringComparer.Ordinal) { ["mode"] = mode };
        string[] arrays = ["excludeDates"];
        string weekdays = "weekdays";
        if (string.Equals(mode, weekdays, StringComparison.Ordinal))
        {
            arrays = ["excludeDates", "includeDates", "weekdays"];
        }
        foreach (string key in arrays)
        {
            JsonElement array = values.Get(key);
            IEnumerable<string> items = [];
            if (array.ValueKind == JsonValueKind.Array)
            {
                items = array.EnumerateArray().Select(item => item.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            }
            timing[key] = items.ToArray();
        }
        foreach (string key in new[] { "start", "end" })
        {
            if (values.ReadText(key) is string instant)
            {
                timing[key] = SchedulePlan.ParseInstant(instant, zone).ToUniversalTime();
            }
        }
        string interval = "interval";
        string once = "once";
        bool calendar = true;
        if (string.Equals(mode, interval, StringComparison.Ordinal))
        {
            timing["intervalMilliseconds"] = values.ReadNumber("intervalMilliseconds", 1000);
            calendar = false;
        }
        else
        {
            if (string.Equals(mode, once, StringComparison.Ordinal))
            {
                timing["at"] = SchedulePlan.ParseInstant(values.ReadText("at")!, zone).ToUniversalTime();
                calendar = false;
            }
            else
            {
                timing["time"] = TimeOnly.ParseExact(values.ReadText("time")!, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        string? timeZone = null;
        string exclusions = "excludeDates";
        if (calendar || ((string[])timing[exclusions]!).Length != 0)
        {
            timeZone = zone.Id;
        }
        return ContractJson.Hash(new { timing, timeZone });
    }

    /// <summary>明示した時差がある日時を、地域のタイムゾーンから独立して扱います。</summary>
    /// <returns>日時に時差が明示されているか。</returns>
    internal static bool HasOffset(string text) => text.EndsWith('Z') || (10 < text.Length && (text[10..].Contains('+', StringComparison.Ordinal) || text[10..].Contains('-', StringComparison.Ordinal)));
}
