using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>監視の開始、起動の試行と結果、上限到達を状態変化ごとに記録します。</summary>
public sealed class ApplicationRecoveryLoggingTests
{
    /// <summary>手動起動と自動復旧の試行・成功を対応付け、巡回だけでは追加記録しません。</summary>
    /// <returns>起動回、実体、回数、操作種別とログレベルの確認です。</returns>
    [Fact]
    public async Task StartsAndRecoveryAttemptsAreLoggedOnceAsync()
    {
        RecordingWorkflowLog log = new();
        using ApplicationBootFixture fixture = new(log: log);
        ProcessDefinition definition = fixture.Definition with { AutomaticMonitoring = true, Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessAutomation automation = new(fixture.Service, "peer", log, fixture.Clock);
        await automation.TickAsync(true);
        ProcessOperationResult initial = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(initial.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        await automation.TickAsync(true);
        await fixture.WaitForStartsAsync(2);
        for (int index = 0; index < 10; index++)
        {
            await automation.TickAsync(true);
        }
        WorkflowLogEntry monitoring = Assert.Single(log.Entries, entry => entry.Message is "Application.MonitoringStarted");
        WorkflowLogEntry starting = Assert.Single(log.Entries, entry => entry.Message is "Application.StartAttempted");
        WorkflowLogEntry started = Assert.Single(log.Entries, entry => entry.Message is "Application.Started");
        WorkflowLogEntry restarting = Assert.Single(log.Entries, entry => entry.Message is "Application.RestartAttempted");
        WorkflowLogEntry restarted = Assert.Single(log.Entries, entry => entry.Message is "Application.Restarted");
        Assert.Single(log.Entries, entry => entry.Message is "Application.Stopped");
        Assert.Equal(6, log.Entries.Count);
        Assert.Equal("INFO", monitoring.Level);
        Assert.Equal(starting.Data!.Json.GetProperty("instanceId").GetGuid(), started.Data!.Json.GetProperty("instanceId").GetGuid());
        Assert.Equal(restarting.Data!.Json.GetProperty("instanceId").GetGuid(), restarted.Data!.Json.GetProperty("instanceId").GetGuid());
        Assert.False(started.Data.Json.GetProperty("automatic").GetBoolean());
        Assert.True(restarted.Data.Json.GetProperty("automatic").GetBoolean());
        Assert.Equal(1, restarting.Data.Json.GetProperty("recoveryAttempts").GetInt32());
        Assert.Equal(0, restarted.Data.Json.GetProperty("recoveryAttempts").GetInt32());
        Assert.Equal("unknown", restarting.Data.Json.GetProperty("outcome").GetString());
        Assert.Equal("succeeded", restarted.Data.Json.GetProperty("outcome").GetString());
        Assert.All(log.Entries, entry =>
        {
            Assert.Equal(definition.Id, entry.Data!.Json.GetProperty("applicationId").GetString());
            Assert.DoesNotContain(definition.ExecutablePath, entry.Data.Json.GetRawText(), StringComparison.Ordinal);
        });
    }

    /// <summary>上限到達は保存された遷移で一度記録し、Core再起動では監視開始だけを追加します。</summary>
    /// <returns>上限状態を再観測しても警告を重複しない確認です。</returns>
    [Fact]
    public async Task ExhaustionIsLoggedOnceWhileMonitoringRestartsAsync()
    {
        RecordingWorkflowLog log = new();
        using ApplicationBootFixture fixture = new(log: log);
        ProcessDefinition definition = fixture.Definition with { AutomaticMonitoring = true, Recovery = new(MaximumAttempts: 0, DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult initial = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(initial.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        ProcessAutomation automation = new(fixture.Service, "peer", log, fixture.Clock);
        for (int index = 0; index < 10; index++)
        {
            await automation.TickAsync(true);
        }
        WorkflowLogEntry exhausted = Assert.Single(log.Entries, entry => entry.Message is "Application.RecoveryExhausted");
        Assert.Equal("WARN", exhausted.Level);
        Assert.Equal("process.recoveryExhausted", exhausted.Data!.Json.GetProperty("reason").GetString());
        fixture.RestartCore();
        ProcessAutomation resumed = new(fixture.Service, "peer", log, fixture.Clock);
        for (int index = 0; index < 10; index++)
        {
            await resumed.TickAsync(true);
        }
        Assert.Single(log.Entries, entry => entry.Message is "Application.RecoveryExhausted");
        Assert.Equal(2, log.Entries.Count(entry => entry.Message is "Application.MonitoringStarted"));
        Assert.Equal(1, fixture.StartAttempts);
    }

    /// <summary>手動の起動でも未実行と結果不明を分け、試行と失敗を記録します。</summary>
    /// <returns>自動操作以外もログから理由を追える確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLaunchFailuresAreLoggedAsync(bool unknown)
    {
        RecordingWorkflowLog log = new();
        using ApplicationBootFixture fixture = new(log: log);
        await fixture.Service.RegisterAsync(fixture.Definition);
        fixture.UnknownStart = unknown;
        fixture.RejectNextStart = !unknown;
        ProcessOperationResult result = await fixture.Service.StartAsync(fixture.Definition.Id);
        Assert.Equal(unknown ? EOutcome.Unknown
                             : EOutcome.NotExecuted, result.Outcome);
        WorkflowLogEntry attempted = Assert.Single(log.Entries, entry => entry.Message is "Application.StartAttempted");
        WorkflowLogEntry failure = Assert.Single(log.Entries, entry => entry.Message is "Application.StartFailed");
        Assert.Equal(attempted.Data!.Json.GetProperty("instanceId").GetGuid(), failure.Data!.Json.GetProperty("instanceId").GetGuid());
        Assert.Equal("ERROR", failure.Level);
        Assert.False(failure.Data!.Json.GetProperty("automatic").GetBoolean());
        Assert.Equal(unknown ? "unknown"
                             : "notExecuted", failure.Data.Json.GetProperty("outcome").GetString());
        Assert.Empty(fixture.App.Starts());
    }

    /// <summary>事前条件の不足では起動を試行せず、同じ巡回警告を重複しません。</summary>
    /// <returns>自動復旧が実行されていない理由を一度だけ記録する確認です。</returns>
    [Fact]
    public async Task MissingLaunchUserIsReportedWithoutRepeatedAttemptLogsAsync()
    {
        RecordingWorkflowLog log = new();
        using ApplicationBootFixture fixture = new(log: log);
        ProcessDefinition definition = fixture.Definition with { AutomaticMonitoring = true, Recovery = new(DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult initial = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(initial.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        fixture.UserAvailable = false;
        ProcessAutomation automation = new(fixture.Service, "peer", log, fixture.Clock);
        for (int index = 0; index < 10; index++)
        {
            await automation.TickAsync(true);
        }
        WorkflowLogEntry warning = Assert.Single(log.Entries, entry => entry.Message is "Application.Automation");
        Assert.Equal("process.interactiveUserUnavailable", warning.Data!.Json.GetProperty("reason").GetString());
        Assert.DoesNotContain(log.Entries, entry => entry.Message is "Application.RestartAttempted");
        Assert.Equal(0, (await fixture.Service.ObserveAsync(definition.Id)).Status!.RecoveryAttempts);
    }
}
