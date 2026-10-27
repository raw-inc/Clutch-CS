using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Nodes.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実プロセスと永続状態で管理境界と故障区切りを検証します。</summary>
public sealed class ProcessServiceTests
{
    /// <summary>起動だけではreadyにならず現起動回の明示signalを待ちます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task StartAndReadyAreSeparateAndStaleSignalIsRejected()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(scope.Definition));
        ProcessOperationResult started = await service.StartAsync(scope.Definition.Id);
        scope.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        Assert.True(started.Status!.Running);
        Assert.Null(started.Status.Ready);
        IActionNode wait = (IActionNode)Assert.Single(ProcessNodeRegistration.Create(service, new WorkflowEventHub()), node => string.Equals(node.Descriptor.Type, "process.waitReady", StringComparison.Ordinal));
        NodeExecutionContext context = NodeTestContext.Create(JsonSerializer.Serialize(new { processId = scope.Definition.Id, timeoutMs = 50 }));
        NodeResult timeout = await wait.ExecuteAsync(context);
        Assert.Equal("process.readyTimeout", timeout.ReasonCode);
        ProcessOperationResult stale = await service.SignalAsync(scope.Definition.Id, new ProcessSignal(Guid.NewGuid(), true, true));
        Assert.Equal(EOutcome.NotExecuted, stale.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await service.SignalAsync(scope.Definition.Id, new ProcessSignal(started.Status.InstanceId, true, true))).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await wait.ExecuteAsync(context)).Outcome);
        ProcessOperationResult repeated = await service.StartAsync(scope.Definition.Id);
        Assert.Equal(started.Status.Identity, repeated.Status!.Identity);
    }

    /// <summary>監視のみへの明示登録更新後は人契機の操作も拒否します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ObserveOnlyRejectsStartStopAndRecoveryEvenAfterRegistrationUpdate()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(scope.Definition);
        ProcessOperationResult started = await service.StartAsync(scope.Definition.Id);
        scope.Track(started);
        RegisteredProcess registered = Assert.Single(await service.ListAsync());
        ProcessDefinition observe = scope.Definition with { Mode = EProcessMode.ObserveOnly, ExistingProcess = started.Status!.Identity };
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(observe, registered.Version));
        Assert.Equal(EOutcome.NotExecuted, (await service.StartAsync(observe.Id)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await service.StopAsync(observe.Id, EProcessStopPolicy.Force, 1)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await service.RecoverAsync(observe.Id)).Outcome);
        Assert.True((await service.ObserveAsync(observe.Id)).Status!.Running);
        Assert.NotEqual(EOutcome.Succeeded, await service.RegisterAsync(scope.Definition, registered.Version));
    }

    /// <summary>再起動しても故障回数と有限復旧試行数を失いません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact(Skip = "起動成功後も試行を累積する旧仕様です。ApplicationRecoveryAttemptTestsで起動成功時のリセットとCore再起動時の未確定試行の保持を検証します。")]
    public async Task FailureEpisodeSurvivesServiceRestartUntilHumanConfirmation()
    {
        using ProcessTestScope scope = new();
        using (ProcessService first = scope.CreateService())
        {
            await first.RegisterAsync(scope.Definition);
            ProcessOperationResult started = await first.StartAsync(scope.Definition.Id);
            scope.Track(started);
            await ProcessTestScope.CrashAsync(started.Status!.Identity!).ConfigureAwait(true);
            Assert.Equal(1, (await first.ObserveAsync(scope.Definition.Id)).Status!.FailureCount);
            Assert.Equal(1, (await first.ObserveAsync(scope.Definition.Id)).Status!.FailureCount);
        }
        using ProcessService restarted = scope.CreateService();
        Assert.Equal(1, (await restarted.ObserveAsync(scope.Definition.Id)).Status!.FailureCount);
        ProcessOperationResult recovery = await restarted.RecoverAsync(scope.Definition.Id);
        scope.Track(recovery);
        Assert.Equal(EOutcome.Succeeded, recovery.Outcome);
        Assert.Equal(1, recovery.Status!.RecoveryAttempts);
        Assert.Equal(1, recovery.Status.FailureCount);
        await ProcessTestScope.CrashAsync(recovery.Status.Identity!).ConfigureAwait(true);
        Assert.Equal(2, (await restarted.ObserveAsync(scope.Definition.Id)).Status!.FailureCount);
        ProcessOperationResult exhausted = await restarted.RecoverAsync(scope.Definition.Id);
        Assert.Equal(EOutcome.NotExecuted, exhausted.Outcome);
        Assert.True(exhausted.Status!.RecoveryExhausted);
        Assert.False(exhausted.Status.Running);
        ProcessOperationResult confirmed = await restarted.ConfirmRecoveryAsync(scope.Definition.Id);
        Assert.Equal(0, confirmed.Status!.FailureCount);
        Assert.Equal(0, confirmed.Status.RecoveryAttempts);
        Assert.False(confirmed.Status.RecoveryExhausted);
    }

    /// <summary>外部停止前の人の抑止は永続し、解除まで復旧しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ManualSuppressionSurvivesExternalStopAndServiceRestart()
    {
        using ProcessTestScope scope = new();
        using (ProcessService first = scope.CreateService())
        {
            await first.RegisterAsync(scope.Definition);
            ProcessOperationResult started = await first.StartAsync(scope.Definition.Id);
            scope.Track(started);
            await first.SetRecoverySuppressedAsync(scope.Definition.Id, true);
            await ProcessTestScope.CrashAsync(started.Status!.Identity!).ConfigureAwait(true);
            Assert.Equal(0, (await first.ObserveAsync(scope.Definition.Id)).Status!.FailureCount);
        }
        using ProcessService restarted = scope.CreateService();
        Assert.True((await restarted.ObserveAsync(scope.Definition.Id)).Status!.RecoverySuppressed);
        Assert.Equal(EOutcome.NotExecuted, (await restarted.RecoverAsync(scope.Definition.Id)).Outcome);
        await restarted.SetRecoverySuppressedAsync(scope.Definition.Id, false);
        ProcessOperationResult recovered = await restarted.RecoverAsync(scope.Definition.Id);
        scope.Track(recovered);
        Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
        Assert.True(recovered.Status!.Running);
    }

    /// <summary>停止方針不足を事前に拒否し、人待ち後の強制終了を実確認します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task StopRequiresPolicyAndPlannedStopNeverAutoRecovers()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(scope.Definition);
        ProcessOperationResult started = await service.StartAsync(scope.Definition.Id);
        scope.Track(started);
        IActionNode stop = (IActionNode)Assert.Single(ProcessNodeRegistration.Create(service, new WorkflowEventHub()), node => string.Equals(node.Descriptor.Type, "process.stop", StringComparison.Ordinal));
        NodeResult missing = await stop.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { processId = scope.Definition.Id })));
        Assert.Equal(EOutcome.NotExecuted, missing.Outcome);
        ProcessOperationResult manual = await service.StopAsync(scope.Definition.Id, EProcessStopPolicy.WaitForManual, 10);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(EOutcome.Failed, manual.Outcome);
            Assert.True(manual.Status!.Running);
        }
        else
        {
            // Unixのsleepは通常SIGTERMで終了し、強制終了を必要としません。
            Assert.Equal(EOutcome.Succeeded, manual.Outcome);
            Assert.False(manual.Status!.Running);
        }
        Assert.Equal(EOutcome.Succeeded, (await service.StopAsync(scope.Definition.Id, EProcessStopPolicy.Force, 10)).Outcome);
        ProcessOperationResult observed = await service.ObserveAsync(scope.Definition.Id);
        Assert.False(observed.Status!.Running);
        Assert.Equal(0, observed.Status.FailureCount);
        Assert.Equal(EOutcome.NotExecuted, (await service.RecoverAsync(scope.Definition.Id)).Outcome);
    }

    /// <summary>生存中の判定不能を復旧せず、確認済み異常だけを明示方針で復旧します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ConfirmedUnhealthyAndStableSignalControlRecoveryEpisode()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        ProcessDefinition definition = scope.Definition with { Recovery = new ProcessRecoveryPolicy(RecoverWhenUnhealthy: true, UnhealthyStopPolicy: EProcessStopPolicy.Force, StopTimeoutMilliseconds: 10) };
        await service.RegisterAsync(definition);
        ProcessOperationResult started = await service.StartAsync(definition.Id);
        scope.Track(started);
        await service.SignalAsync(definition.Id, new ProcessSignal(started.Status!.InstanceId, true, null));
        Assert.Equal(started.Status.Identity, (await service.RecoverAsync(definition.Id)).Status!.Identity);
        await service.SignalAsync(definition.Id, new ProcessSignal(started.Status.InstanceId, false, false));
        ProcessOperationResult recovered = await service.RecoverAsync(definition.Id);
        scope.Track(recovered);
        Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
        Assert.NotEqual(started.Status.Identity, recovered.Status!.Identity);
        Assert.Equal(0, recovered.Status.RecoveryAttempts);
        Assert.Equal(1, recovered.Status.FailureCount);
        ProcessOperationResult stable = await service.SignalAsync(definition.Id, new ProcessSignal(recovered.Status.InstanceId, true, true, true));
        Assert.Equal(0, stable.Status!.RecoveryAttempts);
        await ProcessTestScope.CrashAsync(recovered.Status.Identity!).ConfigureAwait(true);
        Assert.Equal(1, (await service.ObserveAsync(definition.Id)).Status!.FailureCount);
    }

    /// <summary>永続した未確定起動を暗黙再起動せず、人の確認でだけ解決します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task PersistedUnconfirmedLaunchRequiresExplicitResolution()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(scope.Definition);
        await scope.Repository.WriteLocalSnapshotAsync("peer:peer:processStates", scope.Definition.Id, NodeValue.FromObject(new { instanceId = Guid.NewGuid(), launchPending = true }));
        ProcessOperationResult blocked = await service.StartAsync(scope.Definition.Id);
        Assert.Equal(EOutcome.Unknown, blocked.Outcome);
        Assert.Null(blocked.Status!.Identity);
        using ProcessService restarted = scope.CreateService();
        Assert.Equal(EOutcome.Unknown, (await restarted.StartAsync(scope.Definition.Id)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await restarted.ResolveLaunchAsync(scope.Definition.Id, null)).Outcome);
        ProcessOperationResult started = await restarted.StartAsync(scope.Definition.Id);
        scope.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        Assert.NotNull(started.Status!.Identity);
    }

    /// <summary>継続監視の予定停止では管理プロセスを終了せず監視だけを終了します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task WatchNormalStopLeavesApplicationRunning()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        await service.RegisterAsync(scope.Definition);
        ProcessOperationResult started = await service.StartAsync(scope.Definition.Id);
        scope.Track(started);
        ISourceNode watch = Assert.IsAssignableFrom<ISourceNode>(Assert.Single(ProcessNodeRegistration.Create(service, new WorkflowEventHub()), node => string.Equals(node.Descriptor.Type, "process.watch", StringComparison.Ordinal)));
        bool stopped = false;
        List<NodeEmission> emissions = [];
        NodeExecutionContext initial = NodeTestContext.Create(JsonSerializer.Serialize(new { processId = scope.Definition.Id, intervalMs = 1 }));
        NodeSourceContext context = new(initial, () => initial, (emission, _) =>
        {
            emissions.Add(emission);
            stopped = true;
            return ValueTask.CompletedTask;
        }, () => stopped);
        NodeResult result = await watch.RunAsync(context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Single(emissions);
        Assert.True((await service.ObserveAsync(scope.Definition.Id)).Status!.Running);
    }

    /// <summary>未登録対象と再利用されたPIDを操作対象へ広げません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UnregisteredAndMismatchingIdentityAreNotOperated()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        Assert.Equal(EOutcome.NotExecuted, (await service.StartAsync("unknown")).Outcome);
        using Process current = Process.GetCurrentProcess();
        ProcessDefinition definition = scope.Definition with { ExistingProcess = new ProcessIdentity(current.Id, DateTimeOffset.UnixEpoch) };
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(definition));
        Assert.False((await service.ObserveAsync(definition.Id)).Status!.Running);
        Assert.Equal(EOutcome.Succeeded, (await service.StopAsync(definition.Id, EProcessStopPolicy.Force, 1)).Outcome);
        Assert.False(current.HasExited);
    }
}
