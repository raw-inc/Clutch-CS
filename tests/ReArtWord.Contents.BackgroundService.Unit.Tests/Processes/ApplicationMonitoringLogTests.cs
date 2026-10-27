using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>自動起動と観測済み終了のログを、実プロセスと実保存で検証します。</summary>
public sealed class ApplicationMonitoringLogTests
{
    private static readonly string[] initialMessages = ["Application.Started", "Application.Stopped"];
    private static readonly string[] lifecycleMessages = ["Application.Started", "Application.Stopped", "Application.Restarted", "Application.Stopped"];
    private static readonly string[] lifecycleLevels = ["INFO", "WARN", "INFO", "WARN"];

    /// <summary>正常・異常終了をWARNにし、実際の自動起動・復旧だけをINFOで一度記録します。</summary>
    /// <returns>遅延・上限到達・再観測でログを水増ししない確認です。</returns>
    [Theory(Skip = "起動成功後も回数を累積する旧仕様です。ApplicationRecoveryLoggingTestsで起動試行・成功・停止・上限の記録を検証します。")]
    [InlineData(0)]
    [InlineData(17)]
    public async Task ScheduledStartExitAndRecoveryUseRequestedLevelsAsync(int exitCode)
    {
        using ApplicationProcessFixture app = new(exitCode, 100, withArguments: true);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        RecordingWorkflowLog log = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock, log: log);
        ProcessSchedule schedule = new("once", "once", At: clock.GetUtcNow().AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
        ProcessDefinition definition = app.Definition with { Schedules = [schedule], Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 1000) };
        await service.RegisterAsync(definition);
        ProcessAutomation automation = new(service, "peer", timeProvider: clock);
        await automation.TickAsync(true);
        Assert.Empty(log.Entries);
        clock.Advance(TimeSpan.FromSeconds(1));
        await automation.TickAsync(true);
        await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        Assert.Equal(initialMessages, log.Entries.Select(entry => entry.Message));
        for (int index = 0; index < 10; index++)
        {
            await automation.TickAsync(true);
        }
        Assert.Equal(2, log.Entries.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await automation.TickAsync(true);
        await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        await automation.TickAsync(true);
        WorkflowLogEntry[] entries = log.Entries.ToArray();
        Assert.Equal(lifecycleMessages, entries.Select(entry => entry.Message));
        Assert.Equal(lifecycleLevels, entries.Select(entry => entry.Level));
        Assert.Equal(entries[0].Data!.Json.GetProperty("pid").GetInt32(), entries[1].Data!.Json.GetProperty("pid").GetInt32());
        Assert.Equal(entries[2].Data!.Json.GetProperty("pid").GetInt32(), entries[3].Data!.Json.GetProperty("pid").GetInt32());
        Assert.Equal(exitCode, entries[1].Data!.Json.GetProperty("exitCode").GetInt32());
        Assert.Equal(exitCode, entries[3].Data!.Json.GetProperty("exitCode").GetInt32());
        Assert.All(entries, entry =>
        {
            Assert.Equal("peer", entry.PeerId);
            Assert.Equal(definition.Id, entry.Data!.Json.GetProperty("applicationId").GetString());
            Assert.Equal(definition.DisplayName, entry.Data.Json.GetProperty("applicationName").GetString());
            Assert.DoesNotContain(definition.ExecutablePath, entry.Data.Json.GetRawText(), StringComparison.Ordinal);
            Assert.False(entry.Data.Json.TryGetProperty("arguments", out _));
        });
    }

    /// <summary>実行物のない予定も失敗理由付きERRORにし、同じ予定の巡回では再記録しません。</summary>
    /// <returns>副作用未実行と結果不明を混同しない確認です。</returns>
    [Fact]
    public async Task MissingExecutableIsOneErrorForTheScheduledAttemptAsync()
    {
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        RecordingWorkflowLog log = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock, log: log);
        ProcessDefinition definition = scope.Definition with { ExecutablePath = Path.Combine(scope.Root, "missing.exe"), LogicalName = "Unavailable application",
            Schedules = [new("once", "once", At: clock.GetUtcNow().AddSeconds(1).ToString("O", CultureInfo.InvariantCulture))] };
        await service.RegisterAsync(definition);
        ProcessAutomation automation = new(service, "peer", timeProvider: clock);
        await automation.TickAsync(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        for (int index = 0; index < 10; index++)
        {
            await automation.TickAsync(true);
        }
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Application.StartFailed", entry.Message);
        Assert.Equal("ERROR", entry.Level);
        Assert.Equal("process.executableUnavailable", entry.Data!.Json.GetProperty("reason").GetString());
        Assert.Equal("notExecuted", entry.Data.Json.GetProperty("outcome").GetString());
    }

    /// <summary>ログイン待ちは記録せず、ログイン後のPC起動予定だけを一度記録します。</summary>
    /// <returns>Core再起動時の監視再開でも同じ起動をログへ再送しない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginWaitingAndCoreRestartDoNotDuplicateBootLogsAsync(bool skip)
    {
        RecordingWorkflowLog log = new();
        using ApplicationBootFixture fixture = new(log: log);
        await fixture.RegisterAsync(new("boot", "boot", SkipWhenLoggedOut: skip));
        fixture.UserAvailable = false;
        fixture.Reboot(5000);
        for (int index = 0; index < 10; index++)
        {
            await fixture.Automation.TickAsync(true);
        }
        Assert.Empty(log.Entries);
        fixture.UserAvailable = true;
        await fixture.Automation.TickAsync(true);
        if (skip)
        {
            Assert.Empty(log.Entries);
            Assert.Equal(0, fixture.StartAttempts);
            return;
        }
        await fixture.WaitForStartsAsync(1);
        Assert.Equal("INFO", Assert.Single(log.Entries, entry => entry.Message is "Application.Started").Level);
        Assert.Single(log.Entries, entry => entry.Message is "Application.StartAttempted");
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Equal(2, log.Entries.Count);
        await fixture.Service.StopAsync(fixture.Definition.Id, EProcessStopPolicy.Force, 1);
        Assert.Equal("WARN", log.Entries.Last().Level);
        Assert.True(log.Entries.Last().Data!.Json.GetProperty("plannedStop").GetBoolean());
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Equal(3, log.Entries.Count);
    }

    /// <summary>OSに拒否された起動と結果不明の起動をERRORで区別します。</summary>
    /// <returns>失敗した実試行を一件だけ記録する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrUnconfirmedLaunchIsAnErrorAsync(bool unknown)
    {
        RecordingWorkflowLog log = new();
        using ApplicationBootFixture fixture = new(log: log);
        await fixture.RegisterAsync(new("boot", "boot", SkipWhenLoggedOut: true));
        fixture.Reboot();
        fixture.UnknownStart = unknown;
        fixture.RejectNextStart = !unknown;
        for (int index = 0; index < 10; index++)
        {
            await fixture.Automation.TickAsync(true);
        }
        WorkflowLogEntry entry = Assert.Single(log.Entries, item => item.Message is "Application.StartFailed");
        Assert.Equal("Application.StartFailed", entry.Message);
        Assert.Equal("ERROR", entry.Level);
        Assert.Equal(unknown ? "unknown"
                             : "notExecuted", entry.Data!.Json.GetProperty("outcome").GetString());
        Assert.Equal(1, fixture.StartAttempts);
    }

    /// <summary>記録先の故障をアプリの起動失敗へ変換しません。</summary>
    /// <returns>ログ例外の隔離と実起動の確認です。</returns>
    [Fact]
    public async Task LogFailureDoesNotFailTheApplicationAsync()
    {
        RecordingWorkflowLog log = new() { Throw = true };
        using ApplicationBootFixture fixture = new(log: log);
        await fixture.RegisterAsync(new("boot", "boot"));
        fixture.Reboot();
        await fixture.Automation.TickAsync(true);
        await fixture.WaitForStartsAsync(1);
        Assert.Equal(EOutcome.Succeeded, (await fixture.StatusAsync()).Outcome);
        Assert.Single(log.Entries, entry => entry.Message is "Application.Started");
        Assert.Single(log.Entries, entry => entry.Message is "Application.StartAttempted");
    }
}
