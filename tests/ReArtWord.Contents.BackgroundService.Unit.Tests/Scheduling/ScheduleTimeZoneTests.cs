using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>選択した地域の日時と、旧設定の共通ゾーンを区別して発火します。</summary>
public sealed class ScheduleTimeZoneTests
{
    /// <summary>オフセットなしの日時は明示ゾーンで解釈し、旧未指定は共通設定を維持します。</summary>
    /// <returns>出力された絶対時刻の確認です。</returns>
    [Theory]
    [InlineData(null, "2026-10-06T00:00:00Z")]
    [InlineData("", "2026-10-06T00:00:00Z")]
    [InlineData("UTC", "2026-10-06T09:00:00Z")]
    [InlineData("Asia/Tokyo", "2026-10-06T00:00:00Z")]
    [InlineData("Tokyo Standard Time", "2026-10-06T00:00:00Z")]
    public async Task OnceUsesExplicitZoneAndRetainsLegacySharedZoneAsync(string? zone, string expected)
    {
        Dictionary<string, object> settings = new() { ["mode"] = "once", ["at"] = "2026-10-06T09:00:00" };
        if (zone is not null)
        {
            settings["timeZoneId"] = zone;
        }
        List<NodeEmission> emissions = await RunAsync(settings).ConfigureAwait(true);
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), Assert.Single(emissions).Result.Outputs["scheduledAt"].Json.GetDateTimeOffset());
    }

    /// <summary>曜日・期間・例外日を選択した地域の日付に揃えます。</summary>
    /// <returns>日本時間の火曜00:30に一回発火する確認です。</returns>
    [Theory]
    [InlineData("daily")]
    [InlineData("weekdays")]
    public async Task CalendarUsesSelectedZoneForWindowAndDatesAsync(string mode)
    {
        Dictionary<string, object> settings = new()
        {
            ["mode"] = mode, ["timeZoneId"] = "Asia/Tokyo", ["time"] = "00:30", ["weekdays"] = new[] { 1 },
            ["start"] = "2026-10-06T00:00:00", ["end"] = "2026-10-06T01:00:00", ["count"] = 1,
            ["includeDates"] = new[] { "2026-10-06" }, ["excludeDates"] = new[] { "2026-10-05" },
        };
        List<NodeEmission> emissions = await RunAsync(settings, TimeZoneInfo.Utc).ConfigureAwait(true);
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T15:30:00Z", CultureInfo.InvariantCulture), Assert.Single(emissions).Result.Outputs["scheduledAt"].Json.GetDateTimeOffset());
    }

    /// <summary>明示された時差は選択ゾーンで移動せず、除外日だけは地域の暦に従います。</summary>
    /// <returns>絶対日時と地域の除外日の確認です。</returns>
    [Fact]
    public async Task ExplicitOffsetStaysAbsoluteAndExclusionUsesLocalDateAsync()
    {
        Dictionary<string, object> settings = new() { ["mode"] = "once", ["timeZoneId"] = "UTC", ["at"] = "2026-10-06T00:30:00+09:00" };
        List<NodeEmission> emissions = await RunAsync(settings).ConfigureAwait(true);
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T15:30:00Z", CultureInfo.InvariantCulture), Assert.Single(emissions).Result.Outputs["scheduledAt"].Json.GetDateTimeOffset());
        settings["timeZoneId"] = "Asia/Tokyo";
        settings["excludeDates"] = new[] { "2026-10-06" };
        Assert.Empty(await RunAsync(settings, TimeZoneInfo.Utc).ConfigureAwait(true));
    }

    /// <summary>不明な地域や夏時間の欠落・重複時刻を黙って別時刻へ直しません。</summary>
    [Theory]
    [InlineData("Invalid/NotAZone", "2026-10-06T09:00:00", "time.zone.invalid")]
    [InlineData("America/New_York", "2026-03-08T02:30:00", "schedule.invalid")]
    [InlineData("America/New_York", "2026-11-01T01:30:00", "schedule.invalid")]
    public void InvalidZoneAndAmbiguousDatesAreRejected(string zone, string at, string reason)
    {
        NodeDefinition definition = new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "once", at, timeZoneId = zone }));
        Assert.Contains(new ScheduleNode().Validate(definition), issue => string.Equals(issue.Code, reason, StringComparison.Ordinal));
    }

    /// <summary>期限を過ぎた有限予定を実際のSource経路で実行します。</summary>
    /// <returns>通知された時間契機です。</returns>
    private static async Task<List<NodeEmission>> RunAsync(Dictionary<string, object> settings, TimeZoneInfo? shared = null)
    {
        NodeExecutionContext item = NodeTestContext.Create(JsonSerializer.Serialize(settings), time: new FixedClock(DateTimeOffset.Parse("2026-10-07T00:00:00Z", CultureInfo.InvariantCulture)))
            with { TimeZone = shared ?? TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo") };
        List<NodeEmission> emissions = [];
        NodeSourceContext context = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Assert.Equal(EOutcome.Succeeded, (await new ScheduleNode().RunAsync(context, timeout.Token).ConfigureAwait(false)).Outcome);
        return emissions;
    }
}
