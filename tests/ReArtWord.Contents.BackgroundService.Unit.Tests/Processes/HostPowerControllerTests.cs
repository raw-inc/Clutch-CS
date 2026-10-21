using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>OSを操作せず、対象実体・状態・コマンド構築を確認します。</summary>
public sealed class HostPowerControllerTests
{
    /// <summary>確認済みの稼働状態と同じPC実体にだけ操作を渡します。</summary>
    /// <returns>注入した実行境界の検証です。</returns>
    [Theory]
    [InlineData(EPeerStatus.Ready, true)]
    [InlineData(EPeerStatus.Syncing, true)]
    [InlineData(EPeerStatus.Unavailable, false)]
    [InlineData(EPeerStatus.Joining, false)]
    [InlineData(EPeerStatus.Degraded, false)]
    public async Task OnlyConfirmedTargetMayExecuteAsync(EPeerStatus status, bool allowed)
    {
        Guid instance = Guid.NewGuid();
        int calls = 0;
        HostPowerController controller = new("host", instance, () => status, (action, _) =>
        {
            calls++;
            Assert.Equal(EHostPowerAction.Restart, action);
            return Task.FromResult(NodeResult.Success());
        });
        HostPowerRequest request = new(Guid.NewGuid(), "host", instance, EHostPowerAction.Restart);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(request with { HostId = "other" })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(request with { InstanceId = Guid.NewGuid() })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(request with { OperationId = Guid.Empty })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await controller.ExecuteAsync(request with { Action = (EHostPowerAction)100 })).Outcome);
        NodeResult result = await controller.ExecuteAsync(request);
        Assert.Equal(allowed, result.Outcome == EOutcome.Succeeded);
        Assert.Equal(Convert.ToInt32(allowed), calls);
    }

    /// <summary>固定引数だけを使用し、Windowsの強制終了を暗黙に有効にしません。</summary>
    [Theory]
    [InlineData("windows", EHostPowerAction.Restart, "/r /t 0")]
    [InlineData("windows", EHostPowerAction.Shutdown, "/s /t 0")]
    [InlineData("linux", EHostPowerAction.Restart, "reboot --no-block --no-ask-password")]
    [InlineData("linux", EHostPowerAction.Shutdown, "poweroff --no-block --no-ask-password")]
    [InlineData("macos", EHostPowerAction.Restart, "-r now")]
    [InlineData("macos", EHostPowerAction.Shutdown, "-h now")]
    public void CommandUsesFixedNonInteractiveArguments(string platform, EHostPowerAction action, string arguments)
    {
        System.Diagnostics.ProcessStartInfo start = Assert.IsType<System.Diagnostics.ProcessStartInfo>(HostPowerController.Command(action, platform));
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(arguments, string.Join(' ', start.ArgumentList));
        Assert.Null(HostPowerController.Command(action, "unsupported"));
    }
}
