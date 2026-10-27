using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>一アプリケーションの比較保存済み状態と実操作の境界を管理します。</summary>
internal sealed class ProcessSession
{
    private readonly ProcessRegistry registry;
    private readonly ProcessDefinition definition;
    private readonly ProcessOptions options;
    private readonly ProcessHandleMonitor monitor;
    private readonly ProcessReplacementProbe replacements;
    private readonly TimeProvider clock;
    private readonly ProcessLifecyclePublisher events;
    private readonly bool automatic;
    private string? version;
    private ProcessStateRecord state;
    private Guid? launchAttempt;

    /// <summary>未ログイン待機では起動意図や復旧回数を書き込みません。</summary>
    /// <returns>設定したユーザーを起動に使用できるかです。</returns>
    internal bool IsLaunchUserAvailable() => monitor.Platform.IsLaunchUserAvailable(definition);

    /// <summary>排他取得後の固定定義と状態を受け取ります。</summary>
    internal ProcessSession(ProcessRegistry registry, ProcessDefinition definition, ProcessOptions options, string? version, ProcessStateRecord state,
        ProcessHandleMonitor monitor, ProcessReplacementProbe replacements, TimeProvider clock, ProcessLifecyclePublisher events, bool automatic)
    {
        this.registry = registry;
        this.definition = definition;
        this.options = options;
        this.version = version;
        this.state = state;
        this.monitor = monitor;
        this.replacements = replacements;
        this.clock = clock;
        this.events = events;
        this.automatic = automatic;
    }

    /// <summary>実終了を同じ起動回について一度だけ故障履歴へ反映します。</summary>
    /// <returns>取得不能も明示した観測結果です。</returns>
    internal async ValueTask<ProcessOperationResult> ObserveAsync(CancellationToken cancellationToken = default)
    {
        (ProcessStateRecord next, bool? running, string? reason) = await ProcessObservation.ReadAsync(definition, state, options, monitor, replacements, clock, cancellationToken).ConfigureAwait(false);
        if ((version is null || next != state) && !await SaveAsync(next, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.stateSaveUnconfirmed", running);
        }
        return Result(state.ReplacementProblem is not null || state.LaunchPending ? EOutcome.Unknown
                                                                                  : EOutcome.Succeeded, reason, running);
    }

    /// <summary>起動意図を保存してから明示対象を起動します。</summary>
    /// <returns>実プロセスと保存の両方を確認した結果です。</returns>
    // 抑制理由: 失敗ログを残した後も、既存のサービス境界での結果判定と例外のスタックを維持します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "ログ追加で既存の例外処理や操作結果を変更しないため、元の例外をサービス境界へ戻します。")]
    internal async ValueTask<ProcessOperationResult> StartAsync(bool restarting = false, bool recovering = false, CancellationToken cancellationToken = default)
    {
        launchAttempt = null;
        ProcessOperationResult result;
        try
        {
            result = await StartCoreAsync(restarting, recovering, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when ((!recovering || launchAttempt.HasValue) && exception is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            await events.StartFailedAsync(definition, state, restarting, automatic, EOutcome.Unknown, "process.launchUnconfirmed", exception.GetType().FullName, launchAttempt, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        string waiting = "process.replacementWaiting", observeOnly = "process.observeOnly";
        if ((!recovering || launchAttempt.HasValue) && result.Outcome != EOutcome.Succeeded && !string.Equals(result.Reason, waiting, StringComparison.Ordinal) && !string.Equals(result.Reason, observeOnly, StringComparison.Ordinal))
        {
            await events.StartFailedAsync(definition, state, restarting, automatic, result.Outcome, result.Reason, attemptId: launchAttempt, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>起動前の確認と実操作を行い、既起動や確認待ちでは副作用を繰り返しません。</summary>
    /// <returns>起動を開始できなかった理由も区別した結果です。</returns>
    private async ValueTask<ProcessOperationResult> StartCoreAsync(bool restarting, bool recovering, CancellationToken cancellationToken = default)
    {
        if (definition.Mode != EProcessMode.Managed)
        {
            return Result(EOutcome.NotExecuted, "process.observeOnly");
        }
        ProcessOperationResult observed = await ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (observed.Outcome != EOutcome.Succeeded || observed.Status?.Running is null)
        {
            return Result(EOutcome.Unknown, "process.previousStateUnconfirmed");
        }
        if (state.Replacement is not null)
        {
            return Result(EOutcome.NotExecuted, "process.replacementWaiting", observed.Status.Running);
        }
        if (observed.Status.Running == true)
        {
            return observed;
        }
        bool validPath = Path.IsPathFullyQualified(definition.ExecutablePath) && File.Exists(definition.ExecutablePath) && Path.IsPathFullyQualified(definition.WorkingDirectory) && Directory.Exists(definition.WorkingDirectory);
        if (!validPath)
        {
            return Result(EOutcome.NotExecuted, "process.executableUnavailable", false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStateRecord previous = state;

        // 復旧回数は起動直前に加算し、事前条件で拒否された場合は元に戻します。
        ProcessStateRecord pending = state with { InstanceId = Guid.NewGuid(), Identity = null, LaunchPending = true, PlannedStop = false, ExitCounted = false,
            RecoveryAttempts = recovering ? checked(state.RecoveryAttempts + 1)
                                          : state.RecoveryAttempts,
            Ready = null, Healthy = null, ExitCode = null, ExitedAt = null, NextRecoveryAt = null, RestartRequested = restarting, ExitUnobserved = false, Replacement = null, ReplacementProblem = null };
        if (!await SaveAsync(pending, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.launchIntentUnconfirmed");
        }
        ProcessIdentity identity;
        try
        {
            launchAttempt = state.InstanceId;
            await events.StartAttemptedAsync(definition, state, automatic, CancellationToken.None).ConfigureAwait(false);
            identity = monitor.Start(definition);
        }
        catch (ProcessLaunchRejectedException exception)
        {
            if (recovering && exception.LaunchAttempted)
            {
                previous = previous with { RecoveryAttempts = state.RecoveryAttempts };
            }
            if (!await SaveAsync(previous, CancellationToken.None).ConfigureAwait(false))
            {
                return Result(EOutcome.Unknown, "process.stateSaveUnconfirmed", false);
            }
            return Result(EOutcome.NotExecuted, exception.Message, false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result(EOutcome.Unknown, "process.launchUnconfirmed");
        }
        if (!await SaveAsync(state with { Identity = identity, LaunchPending = false, RecoveryAttempts = 0, RecoveryExhausted = false }, CancellationToken.None).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.startedStateUnconfirmed", true);
        }
        return Result(EOutcome.Succeeded, running: true);
    }

    /// <summary>予定停止意図を保存してから明示した停止方針を実行します。</summary>
    /// <returns>実終了確認または人待ち・結果不明です。</returns>
    internal async ValueTask<ProcessOperationResult> StopAsync(EProcessStopPolicy policy, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (definition.Mode != EProcessMode.Managed)
        {
            return Result(EOutcome.NotExecuted, "process.observeOnly");
        }
        if (state.LaunchPending)
        {
            return Result(EOutcome.Unknown, "process.launchUnconfirmed");
        }
        if (!await SaveAsync(state with { PlannedStop = true, Replacement = null, ReplacementProblem = null }, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.stopIntentUnconfirmed");
        }
        if (state.Identity is null)
        {
            return Result(EOutcome.Succeeded, running: false);
        }
        EOutcome outcome = await ProcessOs.StopAsync(state.Identity, policy, timeoutMilliseconds, monitor.Platform, cancellationToken).ConfigureAwait(false);
        if (outcome == EOutcome.Succeeded)
        {
            int? exitCode = monitor.Observe(definition.Id, state.Identity).ExitCode;
            if (!await SaveAsync(state with { Ready = false, Healthy = false, ExitCounted = true, ExitCode = exitCode,
                ExitedAt = clock.GetUtcNow(), NextRecoveryAt = null }, CancellationToken.None).ConfigureAwait(false))
            {
                return Result(EOutcome.Unknown, "process.stoppedStateUnconfirmed", false);
            }
            return Result(EOutcome.Succeeded, running: false);
        }
        return Result(outcome, "process.stopNotConfirmed", ProcessOs.IsRunning(state.Identity));
    }

    /// <summary>明示された現起動回の条件を保存します。</summary>
    /// <returns>古い起動回を拒否した結果です。</returns>
    internal async ValueTask<ProcessOperationResult> SignalAsync(ProcessSignal signal, CancellationToken cancellationToken = default)
    {
        if (signal.InstanceId != state.InstanceId)
        {
            return Result(EOutcome.NotExecuted, "process.staleSignal");
        }
        if (signal.Stable && ProcessOs.IsRunning(state.Identity) != true)
        {
            return Result(EOutcome.NotExecuted, "process.stabilityNotConfirmed");
        }
        ProcessStateRecord next = state with { Ready = signal.Ready, Healthy = signal.Healthy };
        if (signal.Healthy == false && definition.Recovery?.RecoverWhenUnhealthy == true && !state.ExitCounted && !state.PlannedStop && !state.RecoverySuppressed)
        {
            next = next with { FailureCount = checked(state.FailureCount + 1), ExitCounted = true };
        }
        if (signal.Stable)
        {
            next = next with { FailureCount = 0, RecoveryAttempts = 0, RecoveryExhausted = false, NextRecoveryAt = null, ExitCounted = false };
        }
        if (!await SaveAsync(next, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.signalSaveUnconfirmed");
        }
        return Result(EOutcome.Succeeded, running: ProcessOs.IsRunning(state.Identity));
    }

    /// <summary>人の復旧抑止または故障区切り確認を保存します。</summary>
    /// <returns>再起動を越えて維持する設定結果です。</returns>
    internal async ValueTask<ProcessOperationResult> ControlAsync(bool? suppressed, bool confirmRecovery, CancellationToken cancellationToken = default)
    {
        if (definition.Mode != EProcessMode.Managed)
        {
            return Result(EOutcome.NotExecuted, "process.observeOnly");
        }
        ProcessStateRecord next = state;
        if (suppressed.HasValue)
        {
            next = next with { RecoverySuppressed = suppressed.Value };
        }
        if (confirmRecovery)
        {
            next = next with { FailureCount = 0, RecoveryAttempts = 0, RecoveryExhausted = false, NextRecoveryAt = null, ExitCounted = ProcessOs.IsRunning(state.Identity) != true };
        }
        if (!await SaveAsync(next, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.controlSaveUnconfirmed");
        }
        return Result(EOutcome.Succeeded, running: ProcessOs.IsRunning(state.Identity));
    }

    /// <summary>確認済み異常だけを有限または明示無制限の範囲で復旧します。</summary>
    /// <returns>復旧実行、抑止、上限到達または判定不能です。</returns>
    internal async ValueTask<ProcessOperationResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ProcessOperationResult observed = await ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (state.Replacement is not null || definition.Mode != EProcessMode.Managed || state.RecoverySuppressed || state.PlannedStop || state.RecoveryExhausted || state.LaunchPending)
        {
            return Result(EOutcome.NotExecuted, "process.recoveryInhibited", observed.Status?.Running);
        }
        if (observed.Outcome != EOutcome.Succeeded || observed.Status?.Running is null)
        {
            return Result(EOutcome.NotExecuted, "process.recoveryConditionUnknown");
        }
        ProcessRecoveryPolicy policy = definition.Recovery ?? new ProcessRecoveryPolicy();
        bool unhealthy = policy.RecoverWhenUnhealthy && state.Healthy == false;
        if ((observed.Status.Running == true && !unhealthy) || state.Identity is null)
        {
            return observed;
        }
        if (observed.Status.Running == false && !(state.ExitCode is null ? policy.RestartOnUnknownExit
                                                                         : state.ExitCode == 0 ? policy.RestartOnNormalExit
                                                                                               : policy.RestartOnAbnormalExit))
        {
            return Result(EOutcome.NotExecuted, "process.exitRecoveryDisabled", false);
        }
        if (state.ExitUnobserved)
        {
            return Result(EOutcome.NotExecuted, "process.exitUnobserved", false);
        }
        DateTimeOffset now = clock.GetUtcNow();
        if (state.NextRecoveryAt.HasValue && now < state.NextRecoveryAt.Value)
        {
            return Result(EOutcome.NotExecuted, "process.recoveryDelay", observed.Status.Running);
        }
        int? maximum = policy.MaximumAttempts ?? options.DefaultRecoveryAttempts;
        if (policy.Unlimited)
        {
            maximum = null;
        }
        if (maximum.HasValue && maximum.Value <= state.RecoveryAttempts)
        {
            bool saved = await SaveAsync(state with { RecoveryExhausted = true, NextRecoveryAt = null }, cancellationToken).ConfigureAwait(false);
            if (!saved)
            {
                return Result(EOutcome.Unknown, "process.stateSaveUnconfirmed");
            }
            return Result(EOutcome.NotExecuted, "process.recoveryExhausted", observed.Status.Running);
        }
        if (!IsLaunchUserAvailable())
        {
            return Result(EOutcome.NotExecuted, "process.interactiveUserUnavailable", observed.Status.Running);
        }
        if (observed.Status.Running == true)
        {
            if (!policy.UnhealthyStopPolicy.HasValue)
            {
                return Result(EOutcome.NotExecuted, "process.stopPolicyRequired", true);
            }
            ProcessOperationResult stopped = await StopAsync(policy.UnhealthyStopPolicy.Value, policy.StopTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            if (stopped.Outcome != EOutcome.Succeeded)
            {
                return stopped;
            }
        }
        ProcessStateRecord next = state with { PlannedStop = false, NextRecoveryAt = now.AddMilliseconds(policy.DelayMilliseconds ?? options.RecoveryDelayMilliseconds) };
        if (!await SaveAsync(next, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.recoveryIntentUnconfirmed");
        }
        return await StartAsync(restarting: true, recovering: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>人が現物確認した起動結果を未確定意図へ反映します。</summary>
    /// <returns>現物照合と保存を確認した結果です。</returns>
    internal async ValueTask<ProcessOperationResult> ResolveLaunchAsync(ProcessIdentity? identity, CancellationToken cancellationToken = default)
    {
        if (definition.Mode != EProcessMode.Managed || !state.LaunchPending)
        {
            return Result(EOutcome.NotExecuted, "process.noUnconfirmedLaunch");
        }
        if (identity is not null && ProcessOs.IsRunning(identity) != true)
        {
            return Result(EOutcome.NotExecuted, "process.confirmedIdentityUnavailable");
        }
        if (!await SaveAsync(state with { Identity = identity, LaunchPending = false, PlannedStop = identity is null, Ready = null, Healthy = null,
            RecoveryAttempts = identity is null ? state.RecoveryAttempts
                                                : 0, RecoveryExhausted = identity is null && state.RecoveryExhausted,
            ExitCode = null, ExitedAt = null, NextRecoveryAt = null, Replacement = null, ReplacementProblem = null }, cancellationToken).ConfigureAwait(false))
        {
            return Result(EOutcome.Unknown, "process.resolutionSaveUnconfirmed");
        }
        return Result(EOutcome.Succeeded, running: identity is not null);
    }

    /// <summary>比較保存後に確定版だけを次の操作へ引き継ぎます。</summary>
    /// <returns>永続化と再読取の両方を確認できたかです。</returns>
    private async ValueTask<bool> SaveAsync(ProcessStateRecord next, CancellationToken cancellationToken = default)
    {
        if (version is not null && next == state)
        {
            return true;
        }
        EOutcome outcome = await registry.SaveStateAsync(definition.Id, version, next, cancellationToken).ConfigureAwait(false);
        if (outcome != EOutcome.Succeeded)
        {
            return false;
        }
        ProcessStateRecord previous = state;
        (version, state) = await registry.ReadStateAsync(definition, cancellationToken).ConfigureAwait(false);
        if (state != next)
        {
            return false;
        }
        await events.ChangedAsync(definition, previous, state, automatic, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>履歴を含む共通観測結果を組み立てます。</summary>
    /// <returns>生の例外や引数をそのまま出力しない操作結果です。</returns>
    private ProcessOperationResult Result(EOutcome outcome, string? reason = null, bool? running = null)
        => new(outcome, new ProcessStatus(definition.Id, definition.Mode, state.InstanceId, state.Identity, running, state.Ready, state.Healthy, state.RecoverySuppressed, state.PlannedStop, state.FailureCount, state.RecoveryAttempts, state.RecoveryExhausted, reason,
            state.ExitCode, state.ExitedAt, state.NextRecoveryAt, state.ExitUnobserved, state.Replacement?.Deadline, state.ReplacementProblem), reason);
}
