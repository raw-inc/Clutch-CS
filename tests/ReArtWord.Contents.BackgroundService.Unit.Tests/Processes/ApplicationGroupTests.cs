using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>グループの停止境界、順序、保存を実プロセスで確認します。</summary>
public sealed class ApplicationGroupTests
{
    /// <summary>全員の終了前には一人も起動せず、二人目の遅延は最初の起動確認から測ります。</summary>
    /// <returns>実終了と仮想時計の境界確認です。</returns>
    [Fact]
    public async Task RestartStopsEveryoneThenStartsInOrderAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync();
        Assert.Equal(EOutcome.Succeeded, (await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Restart))).Outcome);
        await fixture.TickAsync();
        Assert.False((await fixture.Service.ObserveAsync("player")).Status!.Running);
        Assert.True((await fixture.Service.ObserveAsync("server")).Status!.Running);
        Assert.Equal(2, fixture.Started.Count);
        await fixture.TickAsync();
        Assert.False((await fixture.Service.ObserveAsync("server")).Status!.Running);
        Assert.Equal<string>(["player", "server"], fixture.Stopped);
        await fixture.TickAsync();
        Assert.Equal<string>(["server", "player", "server"], fixture.Started);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        await fixture.TickAsync();
        Assert.Equal(3, fixture.Started.Count);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.TickAsync();
        Assert.Equal<string>(["server", "player", "server", "player"], fixture.Started);
        Assert.Equal(EProcessGroupPhase.Idle, (await fixture.GroupAsync()).Status.Phase);
        Assert.Equal(EOutcome.Succeeded, (await fixture.GroupAsync()).Status.Outcome);
    }

    /// <summary>全体復旧は障害の確認時から待ち、故障していないアプリも停止後に起動します。</summary>
    /// <returns>単体監視と重ねない自動操作です。</returns>
    [Fact]
    public async Task AutomaticWholeRecoveryKeepsDelayAndOwnsMembersAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync();
        await fixture.CrashAsync("server");
        await fixture.TickAsync();
        Assert.Equal("process.groupOperationInProgress", (await fixture.Service.RecoverAsync("server")).Reason);
        ProcessAutomation individual = new(fixture.Service, "peer", timeProvider: fixture.Clock);
        await individual.TickAsync(true);
        Assert.Equal(2, fixture.Started.Count);
        await fixture.TickAsync(2);
        await fixture.TickAsync();
        Assert.Equal(2, fixture.Started.Count);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1000));
        await fixture.TickAsync();
        Assert.Equal(3, fixture.Started.Count);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(2000));
        await fixture.TickAsync();
        Assert.Equal(4, fixture.Started.Count);
        Assert.Equal(EOutcome.Succeeded, (await fixture.GroupAsync()).Status.Outcome);
    }

    /// <summary>個別復旧モードは、生きているアプリを停止せず個別の復旧間隔を守ります。</summary>
    /// <returns>同じPIDを維持した復旧です。</returns>
    [Fact]
    public async Task FailedOnlyRecoveryLeavesHealthyMemberRunningAsync()
    {
        using ApplicationGroupFixture fixture = new();
        fixture.Definition = fixture.Definition with { RecoveryMode = EProcessGroupRecoveryMode.FailedApplications };
        await fixture.RegisterAsync();
        ProcessIdentity? healthy = (await fixture.Service.ObserveAsync("player")).Status!.Identity;
        await fixture.CrashAsync("server");
        await fixture.TickAsync(2);
        Assert.Equal(2, fixture.Started.Count);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1000));
        await fixture.TickAsync();
        Assert.Equal<string>(["server", "player", "server"], fixture.Started);
        Assert.Empty(fixture.Stopped);
        Assert.Equal(healthy, (await fixture.Service.ObserveAsync("player")).Status!.Identity);
    }

    /// <summary>起動後の応答喪失では、Coreを再起動しても後続と同じアプリを起動しません。</summary>
    /// <returns>結果不明を成功や未実行へ置き換えない確認です。</returns>
    [Fact]
    public async Task UnknownLaunchPausesWithoutReplayingAfterCoreRestartAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        fixture.UnknownStart = true;
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        await fixture.TickAsync();
        Assert.Equal(EProcessGroupPhase.Paused, (await fixture.GroupAsync()).Status.Phase);
        Assert.Equal(EOutcome.Unknown, (await fixture.GroupAsync()).Status.Outcome);
        fixture.RestartCore();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.TickAsync(4);
        Assert.Equal<string>(["server"], fixture.Started);
        Assert.False((await fixture.Service.ObserveAsync("player")).Status!.Running);
    }

    /// <summary>起動依頼直前の中断は、起動済みと確認できなければ人の確認を待ちます。</summary>
    /// <returns>永続受付を根拠にした非再送の確認です。</returns>
    [Fact]
    public async Task InterruptedClaimIsNotRetriedAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        ProcessGroupStore store = new(fixture.Storage.Repository, "peer");
        (string? version, ProcessGroupRun run) = await store.ReadRunAsync("show");
        await store.SaveRunAsync("show", version, run with { Claimed = true, Phase = EProcessGroupPhase.Starting });
        fixture.RestartCore();
        await fixture.TickAsync();
        Assert.Equal("processGroup.interruptedOperation", (await fixture.GroupAsync()).Status.Reason);
        Assert.Empty(fixture.Started);
    }

    /// <summary>全員停止の確認後にCoreが再起動しても、残りの起動順と待機時刻を維持します。</summary>
    /// <returns>成功済みの起動を重ねない確認です。</returns>
    [Fact]
    public async Task ConfirmedFirstStartSurvivesCoreRestartAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        await fixture.TickAsync();
        fixture.RestartCore();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        await fixture.TickAsync();
        Assert.Single(fixture.Started);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.TickAsync();
        Assert.Equal<string>(["server", "player"], fixture.Started);
    }

    /// <summary>通常終了しないアプリを残す方針では、全体復旧の起動段階へ進みません。</summary>
    /// <returns>停止失敗の局所化です。</returns>
    [Fact]
    public async Task UnconfirmedStopDoesNotStartAnyMemberAsync()
    {
        using ApplicationGroupFixture fixture = new();
        fixture.Definition = fixture.Definition with { StopPolicy = EProcessStopPolicy.WaitForManual };
        await fixture.RegisterAsync();
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Restart));
        await fixture.TickAsync(4);
        Assert.Equal(EProcessGroupPhase.Paused, (await fixture.GroupAsync()).Status.Phase);
        Assert.Equal(2, fixture.Started.Count);
        Assert.True((await fixture.Service.ObserveAsync("server")).Status!.Running);
        Assert.True((await fixture.Service.ObserveAsync("player")).Status!.Running);
    }

    /// <summary>間隔待ち中に先行アプリが終了したら、依存する後続を起動しません。</summary>
    /// <returns>起動順だけでなく起動確認も守る確認です。</returns>
    [Fact]
    public async Task FailedPredecessorPreventsFollowingStartAsync()
    {
        using ApplicationGroupFixture fixture = new();
        await fixture.RegisterAsync(false);
        await fixture.Service.Groups.CommandAsync(new("show", EProcessGroupCommand.Start));
        await fixture.TickAsync();
        await fixture.CrashAsync("server");
        await fixture.TickAsync();
        Assert.Equal("processGroup.predecessorUnavailable", (await fixture.GroupAsync()).Status.Reason);
        Assert.Single(fixture.Started);
    }
}
