using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>前回起動時の復旧状態が、次のPC起動の遅延を追い越さないことを確認します。</summary>
public sealed class ApplicationBootRecoveryTests
{
    /// <summary>ログイン直後も予定時刻まで待ち、上限到達済みの状態は実起動成功で解除します。</summary>
    /// <returns>実プロセス、保存済みの故障状態、OS経過時間を組み合わせた確認です。</returns>
    [Theory]
    [InlineData(90000, false)]
    [InlineData(120000, false)]
    [InlineData(150000, false)]
    [InlineData(180000, false)]
    [InlineData(90000, true)]
    [InlineData(120000, true)]
    [InlineData(150000, true)]
    [InlineData(180000, true)]
    public async Task PriorFailureWaitsForNewBootScheduleAsync(int delay, bool exhausted)
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot", BootDelayMilliseconds: delay), automatic: true);
        RegisteredProcess registered = Assert.Single(await fixture.Service.ListAsync());
        ProcessDefinition definition = registered.Definition with { Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 1000) };
        Assert.Equal(EOutcome.Succeeded, await fixture.Service.RegisterAsync(definition, registered.Version));
        ProcessOperationResult first = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(first.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        if (exhausted)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.RejectAfterLaunchAttempt = true;
            fixture.RejectNextStart = true;
            Assert.Equal(EOutcome.NotExecuted, (await fixture.Service.RecoverAsync(definition.Id)).Outcome);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await fixture.Service.RecoverAsync(definition.Id)).Status!.RecoveryExhausted);
        }
        int previousAttempts = fixture.StartAttempts;
        fixture.UserAvailable = false;
        fixture.Reboot(1000);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Automation.TickAsync(true);
        fixture.UserAvailable = true;
        fixture.Boot = fixture.Boot!.Value with { Uptime = TimeSpan.FromSeconds(30) };
        await fixture.Automation.TickAsync(true);
        Assert.Equal(previousAttempts, fixture.StartAttempts);
        fixture.Boot = fixture.Boot.Value with { Uptime = TimeSpan.FromMilliseconds(delay - 1) };
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(delay - 30001));
        await fixture.Automation.TickAsync(true);
        Assert.Equal(previousAttempts, fixture.StartAttempts);
        fixture.Boot = fixture.Boot.Value with { Uptime = TimeSpan.FromMilliseconds(delay) };
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(2);
        ProcessOperationResult started = await fixture.Service.ObserveAsync(definition.Id);
        Assert.Equal(previousAttempts + 1, fixture.StartAttempts);
        Assert.Equal(0, started.Status!.RecoveryAttempts);
        Assert.False(started.Status.RecoveryExhausted);
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Equal(previousAttempts + 1, fixture.StartAttempts);
    }

    /// <summary>最初の起動後は、Core再起動や後続予定の待機で通常の復旧を止めません。</summary>
    /// <returns>同じPC起動回の復旧が、後の予定を待たずに行われることです。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecoveryResumesAfterTheFirstBootScheduleAsync(bool anotherSchedule, bool restartCore)
    {
        using ApplicationBootFixture fixture = new();
        ProcessSchedule firstSchedule = new("first", "boot", BootDelayMilliseconds: 90000);
        await fixture.RegisterAsync(firstSchedule, automatic: true);
        RegisteredProcess registered = Assert.Single(await fixture.Service.ListAsync());
        ProcessDefinition definition = registered.Definition with { Recovery = new(DelayMilliseconds: 1000) };
        if (anotherSchedule)
        {
            definition = definition with { Schedules = [firstSchedule, new("later", "boot", BootDelayMilliseconds: 180000)] };
        }
        Assert.Equal(EOutcome.Succeeded, await fixture.Service.RegisterAsync(definition, registered.Version));
        fixture.Reboot(90000);
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(1);
        ProcessOperationResult started = await fixture.Service.ObserveAsync(definition.Id);
        await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        if (restartCore)
        {
            fixture.RestartCore();
        }
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Boot = fixture.Boot!.Value with { Uptime = TimeSpan.FromSeconds(91) };
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(2);
        Assert.Equal(2, fixture.StartAttempts);
    }

    /// <summary>次のPC起動から適用する新規予定は、現在の手動起動後の復旧を妨げません。</summary>
    /// <returns>保存しただけでは起動せず、既存の明示起動と復旧は維持する確認です。</returns>
    [Fact]
    public async Task NewlyRegisteredBootScheduleDoesNotBlockCurrentRecoveryAsync()
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot", BootDelayMilliseconds: 90000), automatic: true);
        await fixture.Automation.TickAsync(true);
        Assert.Equal(0, fixture.StartAttempts);
        ProcessOperationResult started = await fixture.Service.StartAsync(fixture.Definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, fixture.Definition.Id);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(2);
        Assert.Equal(2, fixture.StartAttempts);
    }

    /// <summary>予定受付後・起動前の停止でも、結果不明の予定を自動復旧で代わりに実行しません。</summary>
    /// <returns>保存済み受付とCore再起動をまたいだ非再送の確認です。</returns>
    [Fact]
    public async Task UnconfirmedBootReceiptDoesNotResumePriorRecoveryAsync()
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot", BootDelayMilliseconds: 90000), automatic: true);
        ProcessOperationResult started = await fixture.Service.StartAsync(fixture.Definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, fixture.Definition.Id);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Reboot(120000);
        DurableEntry entry = Assert.Single(await fixture.Storage.Repository.ListAsync("peer:peer:processSchedules"));
        ProcessScheduleCursor cursor = entry.Value!.Json.Deserialize<ProcessScheduleCursor>(ContractJson.Options)!;
        ProcessScheduleCursor accepted = cursor with { BootId = fixture.Boot!.Value.Id, After = fixture.Clock.GetUtcNow(),
            ScheduledAt = fixture.Clock.GetUtcNow(), Outcome = EOutcome.Unknown, Reason = "process.scheduleUnconfirmed" };
        Assert.Equal(EOutcome.Succeeded, await fixture.Storage.Repository.WriteLocalSnapshotAsync(entry.Scope, entry.Key, ContractJson.ToValue(accepted)));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await fixture.Automation.TickAsync(true);
            Assert.Equal(1, fixture.StartAttempts);
            Assert.Equal(EOutcome.Unknown, (await fixture.StatusAsync()).Outcome);
            fixture.RestartCore();
        }
    }
}
