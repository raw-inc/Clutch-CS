using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>予定の時刻・タイムゾーン・既起動時方針を、実起動と永続受付で検証します。</summary>
public sealed class ApplicationScheduleTests
{
    /// <summary>指定した一回を起動し、同じ巡回や所有者再作成では再送しません。</summary>
    /// <returns>引数と永続した受付位置の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnceScheduleStartsWithoutWorkflowAndSurvivesAutomationRestartAsync(bool arguments)
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1, withArguments: arguments);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessSchedule schedule = new("once", "once", At: clock.GetUtcNow().AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
        await service.RegisterAsync(app.Definition with { AutomaticMonitoring = false, Schedules = [schedule] });
        ProcessAutomation automation = new(service, "peer", timeProvider: clock);
        await automation.TickAsync(true);
        Assert.False((await service.ObserveAsync(app.Definition.Id)).Status!.Running);
        clock.Advance(TimeSpan.FromSeconds(1));
        await automation.TickAsync(true);
        ProcessIdentity identity = (await service.ObserveAsync(app.Definition.Id)).Status!.Identity!;
        await ApplicationRecoveryTests.WaitForStartAsync(app);
        await automation.TickAsync(true);
        await new ProcessAutomation(service, "peer", timeProvider: clock).TickAsync(true);
        Assert.Equal(identity, (await service.ObserveAsync(app.Definition.Id)).Status!.Identity);
        Assert.Equal(app.Definition.Arguments, Assert.Single(app.Starts()).Skip(2));
    }

    /// <summary>起動済みの予定は選択通りスキップ、または終了確認後だけ再起動します。</summary>
    /// <returns>意図しない二重起動がないことの確認です。</returns>
    [Theory]
    [InlineData(EProcessRunningPolicy.Skip)]
    [InlineData(EProcessRunningPolicy.Restart)]
    public async Task RunningApplicationUsesConfiguredPolicyAsync(EProcessRunningPolicy policy)
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessSchedule schedule = new("once", "once", At: clock.GetUtcNow().AddSeconds(1).ToString("O", CultureInfo.InvariantCulture), AlreadyRunning: policy,
            StopPolicy: EProcessStopPolicy.Force, StopTimeoutMilliseconds: 1);
        await service.RegisterAsync(app.Definition with { Schedules = [schedule] });
        ProcessOperationResult started = await service.StartAsync(app.Definition.Id);
        await ApplicationRecoveryTests.WaitForStartAsync(app);
        ProcessAutomation automation = new(service, "peer", timeProvider: clock);
        await automation.TickAsync(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await automation.TickAsync(true);
        ProcessOperationResult after = await service.ObserveAsync(app.Definition.Id);
        scope.Track(after);
        Assert.True(after.Status!.Running);
        Assert.Equal(policy == EProcessRunningPolicy.Skip, after.Status.Identity == started.Status!.Identity);
        Guid instance = after.Status.InstanceId;
        await automation.TickAsync(true);
        Assert.Equal(instance, (await service.ObserveAsync(app.Definition.Id)).Status!.InstanceId);
    }

    /// <summary>Coreが動作しない間に過ぎた予定は見送り、次の予定を維持します。</summary>
    /// <returns>一回・毎日・曜日指定をUTC以外で検証します。</returns>
    [Theory]
    [InlineData("once")]
    [InlineData("daily")]
    [InlineData("weekdays")]
    public async Task OfflineOccurrencesAreSkippedInSelectedTimezoneAsync(string mode)
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessSchedule schedule = new("scheduled", mode, "Asia/Tokyo", At: "2026-09-10T19:00:01", Time: "19:00:01", Weekdays: [4]);
        await service.RegisterAsync(app.Definition with { Schedules = [schedule] });
        ProcessAutomation automation = new(service, "peer", timeProvider: clock);
        await automation.TickAsync(true);
        await automation.TickAsync(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        await automation.TickAsync(true);
        Assert.Empty(app.Starts());
        List<ProcessScheduleStatus> statuses = [];
        await foreach (ProcessScheduleStatus status in service.ReadSchedulesAsync(app.Definition with { Schedules = [schedule] }))
        {
            statuses.Add(status);
        }
        ProcessScheduleStatus current = Assert.Single(statuses);
        if (current.NextAt is { } next)
        {
            clock.Advance(next - clock.GetUtcNow());
            await automation.TickAsync(true);
            await ApplicationRecoveryTests.WaitForStartAsync(app);
            Assert.Single(app.Starts());
        }
        else
        {
            Assert.Equal("once", mode);
        }
    }

    /// <summary>予定変更後に古い予定を実行せず、表示名の編集では受付済み回を戻しません。</summary>
    /// <returns>登録更新と予定処理の比較保存確認です。</returns>
    [Fact]
    public async Task EditingAndDisablingSchedulesDoesNotReplayOldOccurrenceAsync()
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessSchedule schedule = new("once", "once", At: clock.GetUtcNow().AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
        ProcessDefinition definition = app.Definition with { Schedules = [schedule] };
        await service.RegisterAsync(definition);
        ProcessAutomation automation = new(service, "peer", timeProvider: clock);
        await automation.TickAsync(true);
        RegisteredProcess saved = Assert.Single(await service.ListAsync());
        await service.RegisterAsync(definition with { Schedules = [schedule with { Enabled = false }] }, saved.Version);
        clock.Advance(TimeSpan.FromSeconds(2));
        await automation.TickAsync(true);
        Assert.Empty(app.Starts());
        saved = Assert.Single(await service.ListAsync());
        await service.RegisterAsync(definition with { LogicalName = "Renamed" }, saved.Version);
        await automation.TickAsync(true);
        Assert.Empty(app.Starts());
    }
}
