using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実OSの電源を操作せず、暦・期限・再起動を越えた重複抑止を確認します。</summary>
public sealed class HostPowerScheduleTests
{
    /// <summary>日次は観測した時刻境界で一回だけ発生し、開始時点や長い中断を遡りません。</summary>
    [Fact]
    public void DailyScheduleDoesNotReplayMissedTimes()
    {
        DateTimeOffset due = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        HostPowerSchedule schedule = new(Guid.NewGuid(), EHostPowerScheduleAction.Wake, "08:00");
        Assert.Equal(due, HostPowerSchedulePlan.Due(schedule, due.AddDays(-1), due.AddSeconds(-1), due.AddSeconds(1)));
        Assert.Null(HostPowerSchedulePlan.Due(schedule, due.AddDays(-1), due.AddMinutes(5), due.AddMinutes(5)));
        Assert.Null(HostPowerSchedulePlan.Due(schedule, due.AddDays(-1), due.AddHours(-1), due.AddHours(1)));
        Assert.Null(HostPowerSchedulePlan.Due(schedule, due.AddSeconds(1), due.AddSeconds(-1), due.AddSeconds(2)));
        Assert.Null(HostPowerSchedulePlan.Due(schedule with { Enabled = false }, due.AddDays(-1), due.AddSeconds(-1), due.AddSeconds(1)));
    }

    /// <summary>曜日の対象外の日には発生せず、指定地域の時刻をUTCの一回へ変換します。</summary>
    [Fact]
    public void WeekdayAndTimezoneAreExplicit()
    {
        DateTimeOffset thursday = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        HostPowerSchedule friday = new(Guid.NewGuid(), EHostPowerScheduleAction.Shutdown, "08:00", Weekdays: [5]);
        Assert.Null(HostPowerSchedulePlan.Due(friday, thursday.AddDays(-1), thursday.AddSeconds(-1), thursday.AddSeconds(1)));
        Assert.Equal(thursday.AddDays(1), HostPowerSchedulePlan.Create(friday, thursday.AddDays(-1)).Next(thursday));
        HostPowerSchedule tokyo = new(Guid.NewGuid(), EHostPowerScheduleAction.Wake, "17:00", "Asia/Tokyo");
        Assert.Equal(thursday, HostPowerSchedulePlan.Due(tokyo, thursday.AddDays(-1), thursday.AddSeconds(-1), thursday.AddSeconds(1)));
    }

    /// <summary>日曜から土曜の外側を、別の日付として解釈しません。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void InvalidWeekdaysAreRejected(int day)
    {
        HostPowerSchedule schedule = new(Guid.NewGuid(), EHostPowerScheduleAction.Wake, "08:00", Weekdays: [day]);
        Assert.Throws<ArgumentException>(() => HostPowerSchedulePlan.Create(schedule, DateTimeOffset.UtcNow));
    }

    /// <summary>同じPCの別Coreと再起動後も、同時刻・過去時刻の停止は再受理しません。</summary>
    [Fact]
    public void PhysicalHostLatchRetainsOnlyTheLatestTime()
    {
        using MediaTestFiles files = new();
        DateTimeOffset first = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.True(HostPowerScheduleLatch.Claim(files.Root, first));
        Assert.False(HostPowerScheduleLatch.Claim(files.Root, first));
        Assert.False(HostPowerScheduleLatch.Claim(files.Root, first.AddDays(-1)));
        Assert.True(HostPowerScheduleLatch.Claim(files.Root, first.AddDays(1)));
        Assert.Equal(8, new FileInfo(Path.Combine(files.Root, "scheduled-shutdown.bin")).Length);
    }

    /// <summary>壊れた受付位置を未実行として扱いません。</summary>
    [Fact]
    public void CorruptedLatchDoesNotAuthorizeShutdown()
    {
        using MediaTestFiles files = new();
        File.WriteAllBytes(Path.Combine(files.Root, "scheduled-shutdown.bin"), [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => HostPowerScheduleLatch.Claim(files.Root, DateTimeOffset.UtcNow));
    }

    /// <summary>遅着・再起動・別PCの要求はOS境界へ渡さず、手動操作の既存契約は維持します。</summary>
    /// <returns>注入した無害な実行境界の呼出回数です。</returns>
    [Fact]
    public async Task ScheduledShutdownChecksDeadlineAndHostLatchAsync()
    {
        DateTimeOffset now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        Guid instance = Guid.NewGuid();
        int executed = 0;
        bool claimed = false;
        HostPowerController controller = new("host", instance, () => EPeerStatus.Ready, (_, _) =>
        {
            executed++;
            return Task.FromResult(NodeResult.Success());
        }, _ =>
        {
            bool fresh = !claimed;
            claimed = true;
            return fresh;
        }, new FixedClock(now));
        HostPowerRequest valid = new(Guid.NewGuid(), "host", instance, EHostPowerAction.Shutdown, now.AddMinutes(1), now);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(valid with { ScheduledAt = now.AddMinutes(-2), NotAfter = now.AddMinutes(-1) })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(valid with { InstanceId = Guid.NewGuid() })).Outcome);
        Assert.False(claimed);
        Assert.Equal(EOutcome.Succeeded, (await controller.ExecuteAsync(valid)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(valid with { OperationId = Guid.NewGuid() })).Outcome);
        Assert.Equal(1, executed);
        Assert.Equal(EOutcome.Succeeded, (await controller.ExecuteAsync(new(Guid.NewGuid(), "host", instance, EHostPowerAction.Shutdown))).Outcome);
        Assert.Equal(2, executed);
    }

    /// <summary>OS時計を変更せず予定の受付時刻を固定します。</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>検証用に固定した時刻を返します。</summary>
        /// <returns>UTCの固定時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
