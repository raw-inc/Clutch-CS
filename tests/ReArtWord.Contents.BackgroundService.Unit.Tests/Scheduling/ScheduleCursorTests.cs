using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>担当変更時に再利用する予定位置と、設定の実際の意味の対応を確認します。</summary>
public sealed class ScheduleCursorTests
{
    /// <summary>時差の表記や未使用設定の変更では、同じ一回指定を再発生させません。</summary>
    /// <returns>設定変更前後の発生回数。</returns>
    [Fact]
    public async Task EquivalentOnceSettingsReuseTheAcceptedOccurrenceAsync()
    {
        MemoryCursor cursor = new();
        List<NodeEmission> emissions = [];
        NodeResult first = await RunAsync("""{"mode":"once","at":"2020-01-01T00:00:00Z"}""", cursor, emissions).ConfigureAwait(true);
        NodeResult second = await RunAsync("""{"mode":"once","at":"2020-01-01T09:00:00+09:00","intervalMilliseconds":777,"time":"03:45","weekdays":[2],"includeDates":["2026-09-16"],"excludeDates":[],"missed":"skip"}""", cursor, emissions).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Equal("alreadyCompleted", second.Route);
        Assert.Single(emissions);
    }

    /// <summary>除外によって一回も発生しない予定も、終了済みとして次の担当へ引き継ぎます。</summary>
    /// <returns>終了通知が一回だけとなる確認。</returns>
    [Fact]
    public async Task EmptyScheduleCompletesOnlyOnceAsync()
    {
        MemoryCursor cursor = new();
        List<NodeEmission> emissions = [];
        string settings = """{"mode":"once","at":"2020-01-01T00:00:00Z","excludeDates":["2020-01-01"]}""";
        Assert.Equal("success", (await RunAsync(settings, cursor, emissions).ConfigureAwait(true)).Route);
        Assert.Equal("alreadyCompleted", (await RunAsync(settings, cursor, emissions).ConfigureAwait(true)).Route);
        Assert.Empty(emissions);
    }

    /// <summary>絶対日時でも除外日は地域の日付なので、タイムゾーンが変われば再評価します。</summary>
    /// <returns>UTCで発生し、日本時間では除外される確認。</returns>
    [Fact]
    public async Task ExclusionDateRetainsItsTimeZoneMeaningAsync()
    {
        MemoryCursor cursor = new();
        List<NodeEmission> emissions = [];
        string settings = """{"mode":"once","at":"2020-01-01T23:00:00Z","excludeDates":["2020-01-02"]}""";
        Assert.Equal("success", (await RunAsync(settings, cursor, emissions, TimeZoneInfo.Utc).ConfigureAwait(true)).Route);
        Assert.Equal("success", (await RunAsync(settings, cursor, emissions, TimeZoneInfo.CreateCustomTimeZone("test-japan", TimeSpan.FromHours(9), "Japan", "Japan")).ConfigureAwait(true)).Route);
        Assert.Single(emissions);
    }

    /// <summary>ノードの明示ゾーンがある予定は共通設定の変更で同じ回を再実行しません。</summary>
    /// <returns>担当交代と共通ゾーン変更を挟んでも一回だけの発火です。</returns>
    [Fact]
    public async Task ExplicitZoneKeepsCursorAcrossSharedZoneChangeAsync()
    {
        MemoryCursor cursor = new();
        List<NodeEmission> emissions = [];
        string settings = """{"mode":"once","at":"2020-01-01T09:00:00","timeZoneId":"Asia/Tokyo","excludeDates":["2020-01-02"]}""";
        Assert.Equal("success", (await RunAsync(settings, cursor, emissions, TimeZoneInfo.Utc).ConfigureAwait(true)).Route);
        Assert.Equal("alreadyCompleted", (await RunAsync(settings, cursor, emissions, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).ConfigureAwait(true)).Route);
        Assert.Single(emissions);
    }

    /// <summary>同じ保存先を使い、別のSource開始として実行します。</summary>
    /// <returns>ノードの終了結果。</returns>
    private static Task<NodeResult> RunAsync(string settings, MemoryCursor cursor, List<NodeEmission> emissions, TimeZoneInfo? zone = null)
    {
        NodeExecutionContext item = NodeTestContext.Create(settings) with { TimeZone = zone ?? TimeZoneInfo.Utc };
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false) { Schedule = cursor };
        return new ScheduleNode().RunAsync(source);
    }

    /// <summary>ノードが選んだ時間指定ごとに、受付済み位置を保持する試験用保存先。</summary>
    private sealed class MemoryCursor : INodeScheduleCursor
    {
        private readonly Dictionary<string, ScheduleCursor> values = new(StringComparer.Ordinal);

        /// <summary>同じ時間指定なら前のSourceの進行位置を返します。</summary>
        /// <returns>記録済みまたは初回の位置。</returns>
        public ValueTask<ScheduleCursor> ReadAsync(string timelineKey, DateTimeOffset initialAnchor, CancellationToken cancellationToken = default)
            => new(values.GetValueOrDefault(timelineKey) ?? new(timelineKey, null, initialAnchor));

        /// <summary>入力を通知する前に受付位置を保持します。</summary>
        /// <returns>更新後の位置。</returns>
        public ValueTask<ScheduleCursor> AdvanceAsync(ScheduleCursor cursor, NodeEmission emission, CancellationToken cancellationToken = default)
        {
            ScheduleCursor next = cursor with { LastScheduledAt = emission.Context.Schedule!.ScheduledAt, Count = cursor.Count + 1 };
            values[cursor.Key] = next;
            return new(next);
        }

        /// <summary>発生対象がない予定の終了を保持します。</summary>
        /// <returns>終了済みの位置。</returns>
        public ValueTask<ScheduleCursor> FinishAsync(ScheduleCursor cursor, CancellationToken cancellationToken = default)
        {
            ScheduleCursor next = cursor with { Exhausted = true };
            values[cursor.Key] = next;
            return new(next);
        }
    }
}
