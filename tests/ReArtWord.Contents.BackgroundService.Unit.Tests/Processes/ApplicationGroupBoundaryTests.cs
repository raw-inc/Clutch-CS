using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>個別設定とグループ操作の境界を確認します。</summary>
public sealed class ApplicationGroupBoundaryTests
{
    /// <summary>操作保留中でも、生存しているメンバーの終了を観測し記録します。</summary>
    /// <returns>自動操作の停止と監視の継続を分けた確認です。</returns>
    [Fact]
    public async Task PausedOperationContinuesObservingMemberExitsAsync()
    {
        using ApplicationGroupFixture fixture = new();
        fixture.Definition = fixture.Definition with { StopPolicy = EProcessStopPolicy.WaitForManual };
        await fixture.RegisterAsync();
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Restart));
        await fixture.TickAsync();
        Assert.Equal(EProcessGroupPhase.Paused, (await fixture.GroupAsync()).Status.Phase);
        await fixture.CrashAsync("server");
        await fixture.TickAsync();
        Assert.Contains(fixture.Log.Entries, entry => string.Equals(entry.Message, "Application.Stopped", StringComparison.Ordinal)
            && string.Equals(entry.Data?.Json.GetProperty("applicationId").GetString(), "server", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Started.Count);
    }

    /// <summary>後続の起動待ちを停止操作へ切り替え、取り消した起動を後から再開しません。</summary>
    /// <returns>操作IDの切替と全員停止の確認です。</returns>
    [Fact]
    public async Task StopSupersedesTheRemainingStartupSequenceAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        ProcessGroupOperationResult start = await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        await fixture.TickAsync();
        ProcessGroupOperationResult stop = await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Stop));
        Assert.Equal(EOutcome.Succeeded, stop.Outcome);
        Assert.NotEqual(start.Status!.OperationId, stop.Status!.OperationId);
        await fixture.TickAsync(2);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.TickAsync(2);
        Assert.Single(fixture.Started);
        Assert.False((await fixture.Service.ObserveAsync("server")).Status!.Running);
        Assert.True((await fixture.GroupAsync()).Status.RecoverySuppressed);
    }

    /// <summary>確認画面を開いた後に変更されたグループへ、古い確認の操作を適用しません。</summary>
    /// <returns>版違いの操作は未実行となる確認です。</returns>
    [Fact]
    public async Task CommandRejectsAChangedDefinitionAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        RegisteredProcessGroup original = await fixture.GroupAsync();
        await fixture.Service.Groups.RegisterAsync(original.Definition with { Name = "renamed" }, original.Version);
        ProcessGroupOperationResult rejected = await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start, original.Version));
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Assert.Empty(fixture.Started);
    }

    /// <summary>保存済みの操作と監視開始を記録し、待機巡回だけでは繰り返しません。</summary>
    /// <returns>実起動ログとグループ受付ログを区別した確認です。</returns>
    [Fact]
    public async Task GroupLogsIdentifyMonitoringAndOperationOutcomesAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        await fixture.TickAsync(2);
        Assert.Single(fixture.Log.Entries, entry => string.Equals(entry.Message, "ApplicationGroup.MonitoringStarted", StringComparison.Ordinal));
        ProcessGroupOperationResult accepted = await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        await fixture.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.TickAsync(2);
        WorkflowLogEntry completed = Assert.Single(fixture.Log.Entries, entry => string.Equals(entry.Message, "ApplicationGroup.OperationCompleted", StringComparison.Ordinal));
        Assert.Equal(accepted.Status!.OperationId, completed.Data!.Json.GetProperty("operationId").GetGuid());
        Assert.Single(fixture.Log.Entries, entry => string.Equals(entry.Message, "ApplicationGroup.OperationStarted", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Log.Entries.Count(entry => string.Equals(entry.Message, "Application.Started", StringComparison.Ordinal)));
    }

    /// <summary>所属の重複と、所属したままの個別登録削除を拒否します。</summary>
    /// <returns>停止を伴わない登録所有の確認です。</returns>
    [Fact]
    public async Task MembershipIsExclusiveAndRequiresExplicitRemovalAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        Assert.Equal(EOutcome.NotExecuted, await fixture.Service.Groups.RegisterAsync(fixture.Definition with { Id = "other" }));
        RegisteredProcess application = (await fixture.Service.ListAsync())[0];
        Assert.Equal(EOutcome.NotExecuted, await fixture.Service.RemoveAsync(application.Definition.Id, application.Version));
        RegisteredProcessGroup group = await fixture.GroupAsync();
        Assert.Equal(EOutcome.NotExecuted, await fixture.Service.Groups.RemoveAsync("show", "old-version"));
        Assert.Equal(EOutcome.Succeeded, await fixture.Service.Groups.RemoveAsync("show", group.Version));
        Assert.Equal(EOutcome.Succeeded, await fixture.Service.RemoveAsync(application.Definition.Id, application.Version));
        Assert.Empty(fixture.Started);
    }

    /// <summary>グループ操作の途中は個別の起動・編集と重ねず、読み取りは継続します。</summary>
    /// <returns>同じアプリケーションへの排他の確認です。</returns>
    [Fact]
    public async Task ActiveOperationBlocksMutationsButPreservesObservationsAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        RegisteredProcess app = (await fixture.Service.ListAsync())[0];
        Assert.Equal("process.groupOperationInProgress", (await fixture.Service.StartAsync(app.Definition.Id)).Reason);
        Assert.Equal(EOutcome.NotExecuted, await fixture.Service.RegisterAsync(app.Definition with { LogicalName = "changed" }, app.Version));
        Assert.Equal(EOutcome.Succeeded, (await fixture.Service.ObserveAsync(app.Definition.Id)).Outcome);
        RegisteredProcessGroup group = await fixture.GroupAsync();
        Assert.Equal(EOutcome.NotExecuted, await fixture.Service.Groups.RegisterAsync(group.Definition with { Name = "changed" }, group.Version));
    }

    /// <summary>手動停止・復旧抑止のアプリを、他メンバーの故障で勝手に起動しません。</summary>
    /// <returns>明示操作の尊重です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlannedStopAndSuppressionPreventWholeGroupRecoveryAsync(bool suppress)
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync();
        if (suppress)
        {
            await fixture.Service.SetRecoverySuppressedAsync("player", true);
        }
        else
        {
            await fixture.Service.StopAsync("player", EProcessStopPolicy.Force, 1);
        }
        await fixture.CrashAsync("server");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.TickAsync(4);
        Assert.Equal(EProcessGroupPhase.Idle, (await fixture.GroupAsync()).Status.Phase);
        Assert.Equal(2, fixture.Started.Count);
    }

    /// <summary>グループ停止後の自動復旧抑止は、Core再起動後も残ります。</summary>
    /// <returns>停止完了の永続確認です。</returns>
    [Fact]
    public async Task GroupStopPersistsSuppressionWithoutStoppingOnRemovalAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync();
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Stop));
        await fixture.TickAsync(2);
        fixture.RestartCore();
        await fixture.TickAsync(3);
        RegisteredProcessGroup group = await fixture.GroupAsync();
        Assert.True(group.Status.RecoverySuppressed);
        Assert.Equal(EOutcome.Succeeded, group.Status.Outcome);
        Assert.Equal(2, fixture.Started.Count);
    }

    /// <summary>未ログインのアプリを起動できない間は、動いているメンバーも停止しません。</summary>
    /// <returns>停止前の起動ユーザー確認です。</returns>
    [Fact]
    public async Task MissingLaunchSessionDoesNotStopOtherMembersAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync();
        await fixture.CrashAsync("server");
        fixture.UserAvailable = false;
        await fixture.TickAsync(3);
        Assert.Empty(fixture.Stopped);
        Assert.True((await fixture.Service.ObserveAsync("player")).Status!.Running);
        Assert.Equal(EProcessGroupPhase.Idle, (await fixture.GroupAsync()).Status.Phase);
    }

    /// <summary>前回の故障を引き継いでも、初回のPC起動予定より先には全体を復旧しません。</summary>
    /// <returns>起動遅延を迂回しない確認です。</returns>
    [Fact]
    public async Task GroupRecoveryDoesNotBypassBootScheduleAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync();
        RegisteredProcess app = (await fixture.Service.ListAsync()).Single(item => string.Equals(item.Definition.Id, "server", StringComparison.Ordinal));
        await fixture.Service.RegisterAsync(app.Definition with { Schedules = [new("boot", "boot", BootDelayMilliseconds: 180000)] }, app.Version);
        fixture.Boot = new("new-boot", TimeSpan.FromSeconds(30));
        await fixture.CrashAsync("server");
        await fixture.TickAsync(4);
        Assert.Equal(2, fixture.Started.Count);
        Assert.Empty(fixture.Stopped);
        Assert.Equal(EProcessGroupPhase.Idle, (await fixture.GroupAsync()).Status.Phase);
    }

    /// <summary>全体操作が予定を一時保留しても、終了後に予定の受付をやり直せます。</summary>
    /// <returns>競合でスケジュールが消えない確認です。</returns>
    [Fact]
    public async Task DueBootScheduleSurvivesAnActiveGroupOperationAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        RegisteredProcess app = (await fixture.Service.ListAsync()).Single(item => string.Equals(item.Definition.Id, "server", StringComparison.Ordinal));
        await fixture.Service.RegisterAsync(app.Definition with { Schedules = [new("boot", "boot", AlreadyRunning: EProcessRunningPolicy.Skip)] }, app.Version);
        fixture.Boot = new("new-boot", TimeSpan.FromSeconds(30));
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        ProcessAutomation individual = new(fixture.Service, "peer", timeProvider: fixture.Clock);
        await individual.TickAsync(true);
        Assert.Empty(fixture.Started);
        await fixture.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.TickAsync();
        await individual.TickAsync(true);
        ProcessDefinition definition = (await fixture.Service.ListAsync()).Single(item => string.Equals(item.Definition.Id, "server", StringComparison.Ordinal)).Definition;
        List<ProcessScheduleStatus> schedules = [];
        await foreach (ProcessScheduleStatus schedule in fixture.Service.ReadSchedulesAsync(definition))
        {
            schedules.Add(schedule);
        }
        Assert.NotNull(Assert.Single(schedules).Outcome);
        Assert.Null(schedules[0].NextAt);
        Assert.Equal(2, fixture.Started.Count);
    }
}
