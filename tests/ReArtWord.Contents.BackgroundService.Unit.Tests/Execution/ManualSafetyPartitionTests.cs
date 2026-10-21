using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;

/// <summary>別UDP到達範囲で受付けた稼働操作を設定競合にせず再統合します。</summary>
public sealed class ManualSafetyPartitionTests
{
    /// <summary>明示離脱側の続行はローカル実体だけに束縛し、通常共有保存へ混ぜません。</summary>
    /// <returns>離脱側の明示解除と再起動失効の確認です。</returns>
    [Fact]
    public async Task DetachedContinuationRemainsLocalAndDoesNotSurviveOwnRestart()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.Peers["a"].DetachAsync();
        fixture.Safety["a"].AutomaticStopCheck = (workflow, scope) => true;
        await fixture.ChangeAndConfirmAsync("a", new(Guid.NewGuid(), "workflow", "node", true));
        ManualSafetyChange continued = new(Guid.NewGuid(), "workflow", "node", false, Continued: true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", continued)).Outcome);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Continued);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).LocalOnly);
        Assert.Empty(await fixture.Repositories["a"].ListAsync("execution:safety"));
        await fixture.StopAsync("a");
        await fixture.StartAsync("a");
        await fixture.Safety["a"].RefreshAsync();
        Assert.False(Assert.Single(fixture.Safety["a"].Current).Continued);
    }

    /// <summary>未観測の停止を残し、再接続後の明示続行だけで観測済み停止を解除します。</summary>
    /// <returns>実UDPの分断相当・独立SQLite・再接続の確認です。</returns>
    [Fact]
    public async Task UnobservedStopSurvivesPartitionAndObservedContinueReleasesIt()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]);
        await fixture.StopAsync("b");
        using UdpClient portProbe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int partitionPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        portProbe.Close();
        await fixture.StartAsync("b", networkPort: partitionPort);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Unavailable)]);
        await fixture.WaitForStatesAsync("b", [("a", EPeerStatus.Unavailable), ("b", EPeerStatus.Ready)]);
        ManualSafetyChange stopped = new(Guid.NewGuid(), "workflow", "node", true);
        ManualSafetyChange continued = new(Guid.NewGuid(), "workflow", "node", false, Continued: true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", stopped)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("b", continued)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("b", new(Guid.NewGuid(), "independent", "node", true))).Outcome);
        RepositorySnapshot first = await fixture.Repositories["a"].ExportAsync();
        RepositorySnapshot second = await fixture.Repositories["b"].ExportAsync();
        Assert.True(await fixture.Repositories["a"].ImportAsync(second));
        Assert.True(await fixture.Repositories["b"].ImportAsync(first));
        await fixture.Safety["a"].RefreshAsync();
        ManualSafetyStatus unordered = fixture.Safety["a"].Current.Single(item => string.Equals(item.WorkflowId, "workflow", StringComparison.Ordinal));
        Assert.True(unordered.Stopped);
        Assert.False(unordered.Continued);
        Assert.Null(unordered.Acceptance);
        Assert.Equal(2, unordered.ActiveOperationIds!.Count);
        await fixture.StopAsync("b");
        await fixture.StartAsync("b");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]);
        await fixture.WaitForStatesAsync("b", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]);
        ManualSafetyChange release = continued with { OperationId = Guid.NewGuid() };
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", release)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", stopped)).Outcome);
        ManualSafetyStatus current = fixture.Safety["a"].Current.Single(item => string.Equals(item.WorkflowId, "workflow", StringComparison.Ordinal));
        Assert.False(current.Stopped);
        Assert.Equal(release.OperationId, current.OperationId);
        Assert.True(fixture.Safety["a"].Current.Single(item => string.Equals(item.WorkflowId, "independent", StringComparison.Ordinal)).Stopped);
        Assert.DoesNotContain(fixture.Peers["a"].GetPeers(), peer => peer.Status == EPeerStatus.Conflict);
    }
}
