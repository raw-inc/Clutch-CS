using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>PC起動、時刻補正、Core再起動と未ログインの境界を実アプリケーションで確認します。</summary>
public sealed class ApplicationBootScheduleTests
{
    /// <summary>既存の日時予定と受付記録へ、旧版が読めない既定フィールドを追加しません。</summary>
    [Fact]
    public void CalendarWireAndStoredCursorKeepLegacyShape()
    {
        ProcessSchedule calendar = new("daily", Time: "09:00");
        System.Text.Json.JsonElement json = System.Text.Json.JsonSerializer.SerializeToElement(calendar, ContractJson.Options);
        Assert.False(json.TryGetProperty("bootDelayMilliseconds", out _));
        Assert.False(json.TryGetProperty("skipWhenLoggedOut", out _));
        ProcessScheduleCursor cursor = new("calendar", DateTimeOffset.UtcNow);
        json = System.Text.Json.JsonSerializer.SerializeToElement(cursor, ContractJson.Options);
        Assert.False(json.TryGetProperty("bootId", out _));
        ProcessSchedule boot = new("boot", "boot", BootDelayMilliseconds: 30000, SkipWhenLoggedOut: true);
        Assert.Equal(boot, ContractJson.Copy(boot));
    }

    /// <summary>保存時には起動せず、次のOS起動の経過時間だけを基準に一度起動します。</summary>
    /// <returns>引数、時計補正、停止後を含むCore再起動の非再送確認です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2000, true)]
    public async Task BootDelayUsesOsUptimeAndNeverRepeatsOnCoreRestartAsync(int delay, bool arguments)
    {
        using ApplicationBootFixture fixture = new(arguments);
        await fixture.RegisterAsync(new("boot", "boot", BootDelayMilliseconds: delay));
        await fixture.Automation.TickAsync(true);
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Empty(fixture.App.Starts());
        fixture.Reboot();
        if (0 < delay)
        {
            await fixture.Automation.TickAsync(true);
            fixture.Clock.Advance(TimeSpan.FromDays(2));
            await fixture.Automation.TickAsync(true);
            Assert.Empty(fixture.App.Starts());
            fixture.Boot = fixture.Boot!.Value with { Uptime = TimeSpan.FromMilliseconds(delay) };
            fixture.Clock.Advance(-TimeSpan.FromDays(4));
        }
        await fixture.Automation.TickAsync(true);
        await ApplicationRecoveryTests.WaitForStartAsync(fixture.App);
        Assert.Equal(fixture.Definition.Arguments, Assert.Single(fixture.App.Starts()).Skip(2));
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Equal(1, fixture.StartAttempts);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Service.StopAsync(fixture.Definition.Id, EProcessStopPolicy.Force, 1)).Outcome);
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Equal(1, fixture.StartAttempts);
        Assert.Null((await fixture.StatusAsync()).NextAt);
        fixture.Reboot(delay + 1000);
        await fixture.Automation.TickAsync(true);
        Assert.Equal(2, fixture.StartAttempts);
        await fixture.WaitForStartsAsync(2);
    }

    /// <summary>未ログイン時は既定で待機し、選択すればそのOS起動回を見送ります。</summary>
    /// <returns>待機巡回による永続書込みや起動要求が増えない確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginPolicySurvivesCoreRestartWithoutWritingWhileWaitingAsync(bool wait)
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot", SkipWhenLoggedOut: !wait));
        fixture.UserAvailable = false;
        fixture.Reboot(5000);
        await fixture.Automation.TickAsync(true);
        IReadOnlyList<DurableEntry> before = await fixture.Storage.Repository.ListAsync("peer:peer:processSchedules");
        IReadOnlyList<DurableEntry> stateBefore = await fixture.Storage.Repository.ListAsync("peer:peer:processStates");
        if (wait)
        {
            Assert.Equal("process.scheduleAwaitingLogin", (await fixture.StatusAsync()).Reason);
        }
        for (int index = 0; index < 100; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Automation.TickAsync(true);
        }
        Assert.Equal(before.Select(entry => entry.Version), (await fixture.Storage.Repository.ListAsync("peer:peer:processSchedules")).Select(entry => entry.Version));
        Assert.Equal(stateBefore.Select(entry => entry.Version), (await fixture.Storage.Repository.ListAsync("peer:peer:processStates")).Select(entry => entry.Version));
        Assert.Equal(0, fixture.StartAttempts);
        fixture.RestartCore();
        fixture.UserAvailable = true;
        await fixture.Automation.TickAsync(true);
        Assert.Equal(wait ? 1
                          : 0, fixture.StartAttempts);
        if (wait)
        {
            await ApplicationRecoveryTests.WaitForStartAsync(fixture.App);
            Assert.Single(fixture.App.Starts());
        }
        else
        {
            Assert.Equal("process.scheduleLoginUnavailable", (await fixture.StatusAsync()).Reason);
        }
    }

    /// <summary>ログアウト競合でOS未起動を確認できた場合だけ同じ起動回を待ち直します。</summary>
    /// <returns>確認済み未実行と結果不明を区別する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyConfirmedLoginRejectionCanResumeAfterClaimAsync(bool unknown)
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot"));
        fixture.Reboot();
        fixture.UnknownStart = unknown;
        fixture.RejectNextStart = !unknown;
        await fixture.Automation.TickAsync(true);
        Assert.Equal(1, fixture.StartAttempts);
        fixture.UnknownStart = false;
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        if (unknown)
        {
            Assert.Equal(1, fixture.StartAttempts);
            Assert.Equal(EOutcome.Unknown, (await fixture.StatusAsync()).Outcome);
            Assert.Empty(fixture.App.Starts());
        }
        else
        {
            Assert.Equal(2, fixture.StartAttempts);
            await ApplicationRecoveryTests.WaitForStartAsync(fixture.App);
        }
    }

    /// <summary>起動後にCoreが遅れてReadyになっても、未受付分だけを一度処理します。</summary>
    /// <returns>保守中の期限経過と論理名変更をまたぐ非再送確認です。</returns>
    [Fact]
    public async Task LateReadinessRunsOnceAndRenameDoesNotRearmAsync()
    {
        using ApplicationBootFixture fixture = new();
        ProcessSchedule schedule = new("boot", "boot", BootDelayMilliseconds: 1000);
        await fixture.RegisterAsync(schedule);
        fixture.Reboot();
        await fixture.Automation.TickAsync(false);
        fixture.Boot = fixture.Boot!.Value with { Uptime = TimeSpan.FromMinutes(1) };
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Automation.TickAsync(true);
        await ApplicationRecoveryTests.WaitForStartAsync(fixture.App);
        await fixture.RegisterAsync(schedule, "Renamed");
        await fixture.Automation.TickAsync(true);
        Assert.Single(fixture.App.Starts());
        Assert.Equal(1, fixture.StartAttempts);
    }

    /// <summary>遅延変更・無効化・再有効化は現在のPC起動を再実行しません。</summary>
    /// <returns>編集を反映した次のOS起動でだけ起動する確認です。</returns>
    [Fact]
    public async Task DelayEditAndReenableWaitForNextBootAsync()
    {
        using ApplicationBootFixture fixture = new();
        ProcessSchedule schedule = new("boot", "boot", BootDelayMilliseconds: 1000);
        await fixture.RegisterAsync(schedule);
        fixture.Reboot();
        await fixture.Automation.TickAsync(true);
        schedule = schedule with { BootDelayMilliseconds = 0 };
        await fixture.RegisterAsync(schedule);
        await fixture.Automation.TickAsync(true);
        await fixture.RegisterAsync(schedule with { Enabled = false });
        await fixture.RegisterAsync(schedule);
        await fixture.Automation.TickAsync(true);
        Assert.Empty(fixture.App.Starts());
        fixture.Reboot();
        await fixture.Automation.TickAsync(true);
        await ApplicationRecoveryTests.WaitForStartAsync(fixture.App);
    }

    /// <summary>OS起動を取得できない場合にCore起動で代用せず、範囲外の遅延も拒否します。</summary>
    /// <returns>未知の起動と登録内容の検証です。</returns>
    [Fact]
    public async Task UnavailableBootAndInvalidDelayAreRejectedAsync()
    {
        using ApplicationBootFixture fixture = new();
        fixture.Boot = null;
        ProcessDefinition definition = fixture.Definition with { Schedules = [new("boot", "boot")] };
        Assert.Equal(EOutcome.NotExecuted, await fixture.Service.RegisterAsync(definition));
        fixture.Reboot();
        foreach (int invalid in new[] { -1, 86400001 })
        {
            Assert.Equal(EOutcome.NotExecuted, await fixture.Service.RegisterAsync(definition with { Schedules = [new("boot", "boot", BootDelayMilliseconds: invalid)] }));
        }
        Assert.Empty(await fixture.Service.ListAsync());
    }

    /// <summary>別の起動機構ですでに動くアプリは設定通り見送るか、一度だけ再起動します。</summary>
    /// <returns>既起動時の分岐と未ログイン待機を混同しない確認です。</returns>
    [Theory]
    [InlineData(EProcessRunningPolicy.Skip)]
    [InlineData(EProcessRunningPolicy.Restart)]
    public async Task ExistingApplicationUsesBootSchedulePolicyAsync(EProcessRunningPolicy policy)
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot", AlreadyRunning: policy, StopPolicy: EProcessStopPolicy.Force, StopTimeoutMilliseconds: 1));
        fixture.Reboot();
        ProcessOperationResult first = await fixture.Service.StartAsync(fixture.Definition.Id);
        await fixture.WaitForStartsAsync(1);
        if (policy == EProcessRunningPolicy.Skip)
        {
            fixture.UserAvailable = false;
        }
        await fixture.Automation.TickAsync(true);
        ProcessOperationResult current = await fixture.Service.ObserveAsync(fixture.Definition.Id);
        Assert.Equal(policy == EProcessRunningPolicy.Skip, first.Status!.Identity == current.Status!.Identity);
        Assert.Null((await fixture.StatusAsync()).NextAt);
        int count = 1;
        if (policy == EProcessRunningPolicy.Restart)
        {
            count = 2;
        }
        await fixture.WaitForStartsAsync(count);
        await fixture.Automation.TickAsync(true);
        Assert.Equal(count, fixture.StartAttempts);
    }

    /// <summary>停止中の終了を復旧監視で再送せず、次のOS起動の明示予定からだけ起動します。</summary>
    /// <returns>自動監視とPC起動予定が同じアプリを二重起動しない確認です。</returns>
    [Fact]
    public async Task AutomaticMonitoringDoesNotDuplicateNextBootStartAsync()
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot"), automatic: true);
        fixture.Reboot();
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(1);
        ProcessIdentity identity = (await fixture.Service.ObserveAsync(fixture.Definition.Id)).Status!.Identity!;
        fixture.RestartCore();
        await ProcessTestScope.CrashAsync(identity);
        fixture.Reboot(5000);
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(2);
        await fixture.Automation.TickAsync(true);
        Assert.Equal(2, fixture.StartAttempts);
    }
}
