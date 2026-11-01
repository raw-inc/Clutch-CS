using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実アプリケーションで終了種別、初回遅延、試行上限とCoreの監視境界を確認します。</summary>
public sealed class ApplicationRecoveryTests
{
    /// <summary>短時間の自明終了でも引数と終了コードを失わず、遅延前には再起動しません。</summary>
    /// <returns>実終了から有限再起動までの確認です。</returns>
    [Theory(Skip = "起動成功で復旧回数をリセットする仕様へ変更。ApplicationRecoveryAttemptTests.SuccessfulSelfExitsRespectDelayWithoutAccruingAttemptsAsyncで検証します。")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(23, false)]
    [InlineData(23, true)]
    public async Task SelfExitPreservesArgumentsAndHonorsFirstDelayAndLimitAsync(int exitCode, bool arguments)
    {
        using ApplicationProcessFixture app = new(exitCode, withArguments: arguments);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessDefinition definition = app.Definition with { Recovery = new(MaximumAttempts: 2, DelayMilliseconds: 1000) };
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(definition));
        ProcessOperationResult first = await service.StartAsync(definition.Id);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ProcessOperationResult stopped = await WaitForExitAsync(service, definition.Id);
            Assert.Equal(exitCode, stopped.Status!.ExitCode);
            Assert.False(stopped.Status.ExitUnobserved);
            Assert.Equal(attempt, stopped.Status.RecoveryAttempts);
            Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            ProcessOperationResult next = await service.RecoverAsync(definition.Id);
            if (attempt < 2)
            {
                Assert.Equal(EOutcome.Succeeded, next.Outcome);
                Assert.NotEqual(first.Status!.InstanceId, next.Status!.InstanceId);
            }
            else
            {
                Assert.True(next.Status!.RecoveryExhausted);
                Assert.Null(next.Status.NextRecoveryAt);
                Assert.Equal("process.recoveryExhausted", next.Reason);
            }
        }
        Assert.Equal(3, app.Starts().Length);
        Assert.All(app.Starts(), entry => Assert.Equal(definition.Arguments, entry.Skip(2)));
    }

    /// <summary>選択しなかった終了種別は、稼働中に捕捉しても復旧しません。</summary>
    /// <returns>通常・異常終了ごとの設定確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task DisabledExitKindDoesNotRestartAsync(int exitCode)
    {
        using ApplicationProcessFixture app = new(exitCode);
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        ProcessDefinition definition = app.Definition with { Recovery = new(DelayMilliseconds: 0, RestartOnNormalExit: exitCode != 0, RestartOnAbnormalExit: exitCode == 0) };
        await service.RegisterAsync(definition);
        await service.StartAsync(definition.Id);
        await WaitForExitAsync(service, definition.Id);
        Assert.Equal("process.exitRecoveryDisabled", (await service.RecoverAsync(definition.Id)).Reason);
        Assert.Single(app.Starts());
    }

    /// <summary>監視所有者を作り直しても生存中の同じPIDへ再接続し、その後の異常終了を復旧します。</summary>
    /// <returns>OS実体を保持した監視再開の確認です。</returns>
    [Fact]
    public async Task RestartedServiceReattachesLiveApplicationAndRecoversLaterExitAsync()
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        ProcessIdentity identity;
        using (ProcessService first = scope.CreateService())
        {
            await first.RegisterAsync(app.Definition with { Recovery = new(DelayMilliseconds: 0) });
            identity = (await first.StartAsync(app.Definition.Id)).Status!.Identity!;
            await WaitForStartAsync(app);
        }
        using ProcessService second = scope.CreateService();
        ProcessOperationResult attached = await second.ObserveAsync(app.Definition.Id);
        Assert.True(attached.Status!.Running);
        Assert.Equal(identity, attached.Status.Identity);
        await ProcessTestScope.CrashAsync(identity);
        ProcessOperationResult next = await second.RecoverAsync(app.Definition.Id);
        scope.Track(next);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
        Assert.NotEqual(identity, next.Status!.Identity);
    }

    /// <summary>監視停止中に終了した実体を異常終了と推測して再起動しません。</summary>
    /// <returns>終了時刻・終了コードが未観測であることの確認です。</returns>
    [Fact]
    public async Task ExitWhileServiceStoppedIsNotReplayedOrRecoveredAsync()
    {
        using ApplicationProcessFixture app = new(exitCode: 12, delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        ProcessIdentity identity;
        using (ProcessService first = scope.CreateService())
        {
            await first.RegisterAsync(app.Definition with { Recovery = new(DelayMilliseconds: 0) });
            identity = (await first.StartAsync(app.Definition.Id)).Status!.Identity!;
            await WaitForStartAsync(app);
        }
        using Process process = Process.GetProcessById(identity.ProcessId);
        app.Release();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await ProcessCompletion.WaitAsync(process, timeout.Token);
        using ProcessService second = scope.CreateService();
        ProcessOperationResult result = await second.RecoverAsync(app.Definition.Id);
        Assert.Equal("process.exitUnobserved", result.Reason);
        Assert.True(result.Status!.ExitUnobserved);
        Assert.Equal(0, result.Status.RecoveryAttempts);
        Assert.Equal(0, result.Status.FailureCount);
        Assert.Single(app.Starts());
    }

    /// <summary>予定停止と抑止中の外部停止を自動復旧しません。</summary>
    /// <returns>永続した運用意図の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualStopAndSuppressionPreventAutomaticRecoveryAsync(bool suppress)
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(app.Definition with { Recovery = new(DelayMilliseconds: 0) });
        ProcessOperationResult started = await service.StartAsync(app.Definition.Id);
        await WaitForStartAsync(app);
        if (suppress)
        {
            await service.SetRecoverySuppressedAsync(app.Definition.Id, true);
            await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, (await service.StopAsync(app.Definition.Id, EProcessStopPolicy.Force, 1)).Outcome);
        }
        ProcessAutomation automation = new(service, "peer");
        await automation.TickAsync(true);
        Assert.False((await service.ObserveAsync(app.Definition.Id)).Status!.Running);
        Assert.Single(app.Starts());
    }

    /// <summary>時計の進行に依存せず、実OS終了が観測されるまで有限待機します。</summary>
    /// <returns>終了コード付きの現在状態です。</returns>
    internal static async Task<ProcessOperationResult> WaitForExitAsync(ProcessService service, string id, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            ProcessOperationResult result = await service.ObserveAsync(id, timeout.Token).ConfigureAwait(false);
            if (result.Status?.Running == false)
            {
                return result;
            }
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("試験アプリケーションが終了しませんでした。");
    }

    /// <summary>正常稼働中の反復監視は保存版を更新せず、終了待ち失敗では二重起動しません。</summary>
    /// <returns>定常IOと停止失敗境界の確認です。</returns>
    [Fact]
    public async Task IdleMonitoringDoesNotWriteAndUnconfirmedStopDoesNotRestartAsync()
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(app.Definition);
        ProcessOperationResult started = await service.StartAsync(app.Definition.Id);
        await WaitForStartAsync(app);
        string version = (await scope.Repository.ReadAsync("peer:peer:processStates", app.Definition.Id))!.Version;
        ProcessAutomation automation = new(service, "peer");
        for (int index = 0; index < 100; index++)
        {
            await automation.TickAsync(true);
        }
        Assert.Equal(version, (await scope.Repository.ReadAsync("peer:peer:processStates", app.Definition.Id))!.Version);
        ProcessOperationResult attempted = await service.RestartAsync(app.Definition.Id, EProcessStopPolicy.WaitForManual, 10);
        scope.Track(attempted);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEqual(EOutcome.Succeeded, attempted.Outcome);
            Assert.Equal(started.Status!.Identity, attempted.Status!.Identity);
            Assert.Single(app.Starts());
        }
    }

    /// <summary>アプリ自身が開始記録を書き終えるまで待ちます。</summary>
    /// <returns>実アプリの起動確認です。</returns>
    internal static async Task WaitForStartAsync(ApplicationProcessFixture app, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (app.Starts().Length == 0)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }
}
