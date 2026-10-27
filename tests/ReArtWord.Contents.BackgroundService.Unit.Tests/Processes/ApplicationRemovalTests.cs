using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>登録削除が実プロセスを止めず、監視・予定・所有Peerの境界を守ることを確認します。</summary>
public sealed class ApplicationRemovalTests
{
    /// <summary>管理対象・監視のみのどちらでも、登録削除は実プロセスを停止しません。</summary>
    /// <returns>実プロセスの生存と削除後の操作拒否です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingRegistrationLeavesTheRunningProcessAloneAsync(bool observeOnly)
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(scope.Definition));
        ProcessOperationResult started = await service.StartAsync(scope.Definition.Id);
        scope.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        RegisteredProcess saved = Assert.Single(await service.ListAsync());
        if (observeOnly)
        {
            Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(scope.Definition with { Mode = EProcessMode.ObserveOnly, ExistingProcess = started.Status!.Identity }, saved.Version));
            saved = Assert.Single(await service.ListAsync());
        }
        using Process running = Process.GetProcessById(started.Status!.Identity!.ProcessId);
        Assert.Equal(EOutcome.Succeeded, await service.RemoveAsync(saved.Definition.Id, saved.Version));
        Assert.False(running.HasExited);
        Assert.Empty(await service.ListAsync());
        Assert.Equal("process.notRegistered", (await service.ObserveAsync(saved.Definition.Id)).Reason);
        Assert.Equal("process.notRegistered", (await service.RecoverAsync(saved.Definition.Id)).Reason);
        Assert.Equal("process.notRegistered", (await service.StopAsync(saved.Definition.Id, EProcessStopPolicy.Force, 1)).Reason);
        Assert.False(running.HasExited);
        Assert.All(await scope.Repository.ListAsync("peer:peer:processStates"), entry => Assert.Null(entry.Value));
        using ProcessService restarted = scope.CreateService();
        Assert.Empty(await restarted.ListAsync());
    }

    /// <summary>古い登録版の削除を拒否し、成功時も別Peerの同名登録は残します。</summary>
    /// <returns>比較保存とPeerローカルの削除境界です。</returns>
    [Fact]
    public async Task StaleRemovalPreservesTheRegistrationStateAndSchedulesAsync()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        using ProcessService other = new(scope.Repository, "other", scope.Options);
        ProcessDefinition definition = scope.Definition with { Schedules = [new("daily", Time: "12:00")] };
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(definition));
        Assert.Equal(EOutcome.Succeeded, await other.RegisterAsync(definition));
        RegisteredProcess old = Assert.Single(await service.ListAsync());
        await service.ObserveAsync(definition.Id);
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(definition with { LogicalName = "Renamed" }, old.Version));
        RegisteredProcess current = Assert.Single(await service.ListAsync());
        Assert.NotEqual(EOutcome.Succeeded, await service.RemoveAsync(definition.Id, old.Version));
        Assert.Equal(current.Version, Assert.Single(await service.ListAsync()).Version);
        Assert.Contains(await scope.Repository.ListAsync("peer:peer:processStates"), entry => entry.Value is not null);
        Assert.Contains(await scope.Repository.ListAsync("peer:peer:processSchedules"), entry => entry.Value is not null);
        Assert.Equal(EOutcome.Succeeded, await service.RemoveAsync(definition.Id, current.Version));
        Assert.All(await scope.Repository.ListAsync("peer:peer:processSchedules"), entry => Assert.Null(entry.Value));
        Assert.Equal(definition.Id, Assert.Single(await other.ListAsync()).Definition.Id);
        Assert.Contains(await scope.Repository.ListAsync("peer:other:processSchedules"), entry => entry.Value is not null);
        Assert.Equal(EOutcome.NotExecuted, await service.RemoveAsync(definition.Id, current.Version));
    }

    /// <summary>巡回へ読み込み済みの登録を削除しても、次の起動予定を実行しません。</summary>
    /// <returns>監視キャッシュの更新と、再起動後も続く削除状態です。</returns>
    [Fact]
    public async Task RemovingCachedRegistrationCancelsFutureAutomaticStartsAsync()
    {
        using ApplicationBootFixture fixture = new();
        await fixture.RegisterAsync(new("boot", "boot", BootDelayMilliseconds: 1000), automatic: true);
        fixture.Reboot();
        await fixture.Automation.TickAsync(true);
        RegisteredProcess saved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(EOutcome.Succeeded, await fixture.Service.RemoveAsync(saved.Definition.Id, saved.Version));
        fixture.Boot = fixture.Boot!.Value with { Uptime = TimeSpan.FromSeconds(2) };
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Automation.TickAsync(true);
        fixture.RestartCore();
        await fixture.Automation.TickAsync(true);
        Assert.Equal(0, fixture.StartAttempts);
        Assert.Empty(fixture.App.Starts());
        Assert.Empty(await fixture.Service.ListAsync());
    }

    /// <summary>離脱中の編集制限は、登録追加と削除へ同じように適用します。</summary>
    [Fact]
    public void RemovalRespectsDetachedEditingPolicy()
    {
        Assert.Equal("Detached.EditDenied", DetachedManagementPolicy.GetProblem("processes.remove", true, new(Edit: false)));
        Assert.Null(DetachedManagementPolicy.GetProblem("processes.remove", true, new(Edit: true)));
        Assert.Null(DetachedManagementPolicy.GetProblem("processes.remove", false, new(Edit: false)));
    }
}
