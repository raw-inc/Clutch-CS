using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>終了確定から自動復旧までに現れた後継を実プロセスで確認します。</summary>
public sealed class ApplicationRecoveryReplacementTests
{
    /// <summary>Core再起動後の復旧でも、以前から併存していた別の実体を後継へ取り込みません。</summary>
    /// <returns>最終生存確認より前のプロセスを除外し続ける確認です。</returns>
    [Fact]
    public async Task RecoveryAfterMonitorRestartKeepsTheOriginalSuccessorBoundaryAsync()
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService first = new(scope.Repository, "peer", scope.Options, clock);
        await first.RegisterAsync(app.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 15000) });
        ProcessOperationResult initial = await first.StartAsync(app.Definition.Id);
        using Process existing = ApplicationReplacementTests.StartSuccessor(app, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (app.Starts().Length < 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        await first.ObserveAsync(app.Definition.Id);
        await ProcessTestScope.CrashAsync(initial.Status!.Identity!, timeout.Token);
        await ApplicationRecoveryTests.WaitForExitAsync(first, app.Definition.Id, timeout.Token);
        using ProcessService restarted = new(scope.Repository, "peer", scope.Options, clock);
        clock.Advance(TimeSpan.FromSeconds(15));
        ProcessOperationResult result = await restarted.RecoverAsync(app.Definition.Id, timeout.Token);
        scope.Track(result);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.NotEqual(existing.Id, result.Status!.Identity!.ProcessId);
        Assert.NotEqual(initial.Status.Identity, result.Status.Identity);
        while (app.Starts().Length < 3)
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(3, app.Starts().Length);
    }

    /// <summary>OSに起動を拒否された後の再試行待ちにも、外部で起動した後継を確認します。</summary>
    /// <returns>再試行の前に後継へ監視を移す確認です。</returns>
    [Fact]
    public async Task RejectedLaunchKeepsSuccessorObservationForItsRetryDelayAsync()
    {
        using ApplicationBootFixture fixture = new();
        await fixture.Service.RegisterAsync(fixture.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 1000) });
        ProcessOperationResult first = await fixture.Service.StartAsync(fixture.Definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(first.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, fixture.Definition.Id);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.RejectNextStart = true;
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Service.RecoverAsync(fixture.Definition.Id)).Outcome);
        using Process successor = ApplicationReplacementTests.StartSuccessor(fixture.App, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await ApplicationReplacementTests.WaitAsync(fixture.Service, fixture.Definition.Id, result => result.Status?.Identity?.ProcessId == successor.Id, timeout.Token);
        Assert.Equal(2, fixture.StartAttempts);
        await fixture.WaitForStartsAsync(2);
    }

    /// <summary>終了確認待ちが0でも、確認期限後でも、復旧待機中の後継へ監視を移します。</summary>
    /// <returns>終了通知後の引継ぎと追加起動の抑止です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public async Task SuccessorDuringRecoveryDelayIsAdoptedAsync(int confirmation)
    {
        using ApplicationProcessFixture app = new(exitCode: 17, withArguments: true);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        RecordingWorkflowLog log = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock, log: log);
        ProcessDefinition definition = app.Definition with { RestartDetection = new(confirmation), Recovery = new(DelayMilliseconds: 15000) };
        await service.RegisterAsync(definition);
        await service.StartAsync(definition.Id);
        await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        clock.Advance(TimeSpan.FromMilliseconds(confirmation));
        ProcessOperationResult stopped = await service.ObserveAsync(definition.Id);
        Assert.NotNull(stopped.Status!.ExitedAt);
        Assert.Single(log.Entries, entry => entry.Message is "Application.Stopped");
        clock.Advance(TimeSpan.FromSeconds(9));
        using Process successor = ApplicationReplacementTests.StartSuccessor(app, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        ProcessOperationResult adopted = await ApplicationReplacementTests.WaitAsync(service, definition.Id,
            result => result.Status?.Identity?.ProcessId == successor.Id && app.Starts().Length == 2, timeout.Token);
        Assert.True(adopted.Status!.Running);
        Assert.Null(adopted.Status.NextRecoveryAt);
        Assert.Null(adopted.Status.ExitedAt);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(successor.Id, (await service.RecoverAsync(definition.Id)).Status!.Identity!.ProcessId);
        Assert.Equal(2, app.Starts().Length);
        Assert.Single(log.Entries, entry => entry.Message is "Application.Restarted");
        Assert.DoesNotContain(log.Entries, entry => entry.Message is "Application.RestartAttempted");
    }

    /// <summary>終了を確定していたCoreを作り直しても、残りの復旧待ちで後継を引き継ぎます。</summary>
    /// <returns>復旧待ちの照合条件を永続保存していることの確認です。</returns>
    [Fact]
    public async Task RecoveryObservationSurvivesMonitorRecreationAsync()
    {
        using ApplicationProcessFixture app = new();
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using (ProcessService first = new(scope.Repository, "peer", scope.Options, clock))
        {
            await first.RegisterAsync(app.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 15000) });
            await first.StartAsync(app.Definition.Id);
            Assert.NotNull((await ApplicationRecoveryTests.WaitForExitAsync(first, app.Definition.Id)).Status!.ExitedAt);
        }
        using Process successor = ApplicationReplacementTests.StartSuccessor(app, false);
        using ProcessService restarted = new(scope.Repository, "peer", scope.Options, clock);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await ApplicationReplacementTests.WaitAsync(restarted, app.Definition.Id,
            result => result.Status?.Identity?.ProcessId == successor.Id && app.Starts().Length == 2, timeout.Token);
        Assert.Equal(2, app.Starts().Length);
    }

    /// <summary>復旧期限を過ぎても候補が複数なら追加起動せず、一意になった後継だけを引き継ぎます。</summary>
    /// <returns>曖昧な照合の保留と解除です。</returns>
    [Fact]
    public async Task AmbiguousSuccessorsHoldRecoveryBeyondItsDeadlineAsync()
    {
        using ApplicationProcessFixture app = new();
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        await service.RegisterAsync(app.Definition with { RestartDetection = new(0, EProcessRestartMatch.ExecutableOnly), Recovery = new(DelayMilliseconds: 15000) });
        await service.StartAsync(app.Definition.Id);
        await ApplicationRecoveryTests.WaitForExitAsync(service, app.Definition.Id);
        using Process one = ApplicationReplacementTests.StartSuccessor(app, false);
        using Process two = ApplicationReplacementTests.StartSuccessor(app, true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (app.Starts().Length < 3)
        {
            await Task.Delay(10, timeout.Token);
        }
        await ApplicationReplacementTests.WaitAsync(service, app.Definition.Id, result => result.Status?.ReplacementProblem is "process.replacementAmbiguous", timeout.Token);
        clock.Advance(TimeSpan.FromSeconds(16));
        ProcessOperationResult blocked = await service.RecoverAsync(app.Definition.Id);
        Assert.NotEqual(EOutcome.Succeeded, blocked.Outcome);
        Assert.Equal("process.replacementAmbiguous", blocked.Status!.ReplacementProblem);
        Assert.Equal(3, app.Starts().Length);
        two.Kill(true);
        await ProcessCompletion.WaitAsync(two, timeout.Token);
        await ApplicationReplacementTests.WaitAsync(service, app.Definition.Id, result => result.Status?.Identity?.ProcessId == one.Id, timeout.Token);
        Assert.Equal(3, app.Starts().Length);
    }

    /// <summary>明示起動では直前の候補なしキャッシュを使わず、後から現れた後継を照会します。</summary>
    /// <returns>復旧待機を手動で短縮する場合も実体を追加しない確認です。</returns>
    [Fact]
    public async Task ExplicitStartRefreshesSuccessorObservationAsync()
    {
        using ApplicationProcessFixture app = new();
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(app.Definition with { RestartDetection = new(0, EProcessRestartMatch.ExecutableOnly), Recovery = new(DelayMilliseconds: 15000) });
        await service.StartAsync(app.Definition.Id);
        await ApplicationRecoveryTests.WaitForExitAsync(service, app.Definition.Id);
        await service.ObserveAsync(app.Definition.Id);
        using Process successor = ApplicationReplacementTests.StartSuccessor(app, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (app.Starts().Length < 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        ProcessOperationResult result = await service.StartAsync(app.Definition.Id);
        Assert.Equal(successor.Id, result.Status!.Identity!.ProcessId);
        Assert.True(result.Status.Running);
        Assert.Equal(2, app.Starts().Length);
    }

    /// <summary>後継がなければ元の期限で再起動し、照合のための待ち時間を足しません。</summary>
    /// <returns>0msの終了確認と15秒の復旧間隔を保つ確認です。</returns>
    [Fact]
    public async Task MissingSuccessorDoesNotExtendRecoveryDelayAsync()
    {
        using ApplicationProcessFixture app = new();
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        await service.RegisterAsync(app.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 15000) });
        ProcessOperationResult initial = await service.StartAsync(app.Definition.Id);
        ProcessOperationResult stopped = await ApplicationRecoveryTests.WaitForExitAsync(service, app.Definition.Id);
        clock.Advance(TimeSpan.FromMilliseconds(14999));
        ProcessOperationResult pending = await service.RecoverAsync(app.Definition.Id);
        Assert.Equal(stopped.Status!.NextRecoveryAt, pending.Status!.NextRecoveryAt);
        Assert.Equal("process.recoveryDelay", pending.Reason);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        ProcessOperationResult started = await service.RecoverAsync(app.Definition.Id);
        scope.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        Assert.NotEqual(initial.Status!.Identity, started.Status!.Identity);
        await ApplicationRecoveryTests.WaitForExitAsync(service, app.Definition.Id);
        Assert.Equal(2, app.Starts().Length);
    }

    /// <summary>待機開始後の照合設定変更を、その待機回へ遡及しません。</summary>
    /// <returns>保存した実行物・引数条件の固定です。</returns>
    [Fact]
    public async Task MatchingPolicyIsCapturedForTheRecoveryDelayAsync()
    {
        using ApplicationProcessFixture app = new(withArguments: true);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessDefinition definition = app.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 15000) };
        await service.RegisterAsync(definition);
        ProcessOperationResult initial = await service.StartAsync(definition.Id);
        await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        RegisteredProcess registration = Assert.Single(await service.ListAsync());
        await service.RegisterAsync(definition with { RestartDetection = new(0, EProcessRestartMatch.ExecutableOnly) }, registration.Version);
        using Process successor = ApplicationReplacementTests.StartSuccessor(app, true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (app.Starts().Length < 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        ProcessOperationResult observed = await service.ObserveAsync(definition.Id);
        Assert.Equal(initial.Status!.Identity, observed.Status!.Identity);
        Assert.False(observed.Status.Running);
        Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
    }

    /// <summary>復旧の抑止中や対象外の終了では、新たな復旧待ちの探索を始めません。</summary>
    /// <returns>0msで無効にした終了確認を、復旧対象外の登録へ適用しない確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InhibitedRecoveryDoesNotAdoptAReplacementAsync(bool enabled, bool suppressed)
    {
        using ApplicationProcessFixture app = new();
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(app.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 15000, RestartOnNormalExit: enabled) });
        ProcessOperationResult first = await service.StartAsync(app.Definition.Id);
        await service.SetRecoverySuppressedAsync(app.Definition.Id, suppressed);
        await ApplicationRecoveryTests.WaitForExitAsync(service, app.Definition.Id);
        using Process successor = ApplicationReplacementTests.StartSuccessor(app, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (app.Starts().Length < 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        ProcessOperationResult observed = await service.ObserveAsync(app.Definition.Id);
        Assert.Equal(first.Status!.Identity, observed.Status!.Identity);
        Assert.False(observed.Status.Running);
        Assert.Equal(EOutcome.NotExecuted, (await service.RecoverAsync(app.Definition.Id)).Outcome);
    }
}
