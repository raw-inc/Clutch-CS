using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>UTC既定・地域日付・夏時間・利用者の書式を、固定した時計で検証します。</summary>
public sealed class CurrentTimeNodeTests
{
    /// <summary>時差による日付変更と書式別名・引用を正確に扱います。</summary>
    /// <returns>指定した瞬間から得た日時文字列です。</returns>
    [Theory]
    [InlineData("UTC", "HH:mm:ss.fff-YYYY/MM/DD", "23:04:05.678-2026/10/02")]
    [InlineData("Asia/Tokyo", "HH:mm:ss.fff-YYYY/MM/DD", "08:04:05.678-2026/10/03")]
    [InlineData("Tokyo Standard Time", "yyyy/MM/dd HH:mm:ss.fff zzz", "2026/10/03 08:04:05.678 +09:00")]
    [InlineData("UTC", "'YYYY/DD' YYYY-MM-DD", "YYYY/DD 2026-10-02")]
    [InlineData("UTC", "\"YYYY\" YY/MM/DD", "YYYY 26/10/02")]
    [InlineData("UTC", "\\Y\\Y\\Y\\Y yyyy-MM-dd", "YYYY 2026-10-02")]
    [InlineData("UTC", "o", "2026-10-02T23:04:05.6780000+00:00")]
    public async Task FormatsTheRequestedZoneAndPatternAsync(string zone, string format, string expected)
    {
        CurrentClock clock = new(DateTimeOffset.Parse("2026-10-02T23:04:05.678Z", CultureInfo.InvariantCulture));
        string settings = JsonSerializer.Serialize(new { timeZoneId = zone, format });
        NodeResult result = await new CurrentTimeNode().ExecuteAsync(NodeTestContext.Create(settings, time: clock)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].GetString());
        Assert.Equal(1, clock.Reads);
    }

    /// <summary>保存・型推論時に固定値化せず、既定はクラスタ地域にかかわらずUTCです。</summary>
    /// <returns>二回の実行で取得した独立した時刻です。</returns>
    [Fact]
    public async Task ReadsTheClockOnlyWhenExecutedAndDefaultsToUtcAsync()
    {
        CurrentTimeNode node = new();
        CurrentClock clock = new(DateTimeOffset.Parse("2026-10-02T23:04:05.678Z", CultureInfo.InvariantCulture));
        NodeExecutionContext context = NodeTestContext.Create(time: clock) with { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo") };
        NodeDefinition definition = new("now", node.Descriptor.Type, context.Settings);
        Assert.Empty(node.Validate(definition));
        NodeDescriptor descriptor = new NodeRegistry(SchedulingNodeRegistration.Create()).Descriptors.Single(item => string.Equals(item.Type, node.Descriptor.Type, StringComparison.Ordinal));
        Assert.False(descriptor.CanEvaluateStatically);
        Assert.Equal("string", new WorkflowTypeInference(new("flow", "Now", [definition], []), [descriptor]).PortType("now", "value", true)!.Type);
        Assert.Equal(0, clock.Reads);
        NodeResult first = await node.ExecuteAsync(context).ConfigureAwait(true);
        clock.Now = clock.Now.AddMilliseconds(1);
        NodeResult second = await node.ExecuteAsync(context).ConfigureAwait(true);
        Assert.Equal("2026-10-02 23:04:05.678", first.Outputs["value"].GetString());
        Assert.Equal("2026-10-02 23:04:05.679", second.Outputs["value"].GetString());
        Assert.Equal(2, clock.Reads);
    }

    /// <summary>夏時間の同じ現地時刻を、正しいUTCオフセットで区別します。</summary>
    /// <returns>米国の夏時間終了前後の変換です。</returns>
    [Theory]
    [InlineData("2026-11-01T05:30:00Z", "2026-11-01 01:30 -04:00")]
    [InlineData("2026-11-01T06:30:00Z", "2026-11-01 01:30 -05:00")]
    public async Task UsesDaylightSavingRulesForTheSelectedZoneAsync(string instant, string expected)
    {
        CurrentClock clock = new(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));
        NodeResult result = await new CurrentTimeNode().ExecuteAsync(NodeTestContext.Create("""{"timeZoneId":"America/New_York","format":"yyyy-MM-dd HH:mm zzz"}""", time: clock)).ConfigureAwait(true);
        Assert.Equal(expected, result.Outputs["value"].GetString());
    }

    /// <summary>不正設定を別の地域・書式へ置き換えず、取得前に拒否します。</summary>
    /// <returns>固定した理由と未出力の確認です。</returns>
    [Theory]
    [InlineData("No/Such_Zone", "yyyy", "time.zone.invalid")]
    [InlineData("UTC", "'unclosed", "time.format.invalid")]
    [InlineData("UTC", "HH:mm:ss.ffffffff", "time.format.invalid")]
    [InlineData("UTC", "Q", "time.format.invalid")]
    public async Task InvalidSettingsDoNotReadTheClockAsync(string zone, string format, string reason)
    {
        CurrentClock clock = new(DateTimeOffset.UnixEpoch);
        NodeResult result = await new CurrentTimeNode().ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { timeZoneId = zone, format }), time: clock)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
        Assert.Equal(0, clock.Reads);
    }

    /// <summary>一件に必要な時刻取得だけを観測する時計です。</summary>
    private sealed class CurrentClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>次の実行が観測する現在時刻です。</summary>
        internal DateTimeOffset Now
        {
            get; set;
        } = now;

        /// <summary>現在時刻を取得した回数です。</summary>
        internal int Reads
        {
            get; private set;
        }

        /// <summary>外部時計に依存しない時刻を返します。</summary>
        /// <returns>試験で指定した一つの瞬間です。</returns>
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return Now;
        }
    }
}
