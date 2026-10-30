using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>生存・確認待ち・終了確定の遷移を計算し、OS実体の照合を一つに集約します。</summary>
internal static class ProcessObservation
{
    /// <summary>監視していない間の終了を再送せず、自律再起動の候補が一意なときだけ引き継ぎます。</summary>
    /// <returns>比較保存する次状態、生存観測と保留理由です。</returns>
    internal static async ValueTask<(ProcessStateRecord State, bool? Running, string? Reason)> ReadAsync(ProcessDefinition definition, ProcessStateRecord state,
        ProcessOptions options, ProcessHandleMonitor monitor, ProcessReplacementProbe replacements, TimeProvider clock, bool preparingLaunch = false, CancellationToken cancellationToken = default)
    {
        (bool? running, int? exitCode, bool observed) = monitor.Observe(definition.Id, state.Identity);
        if (state.LaunchPending)
        {
            return (state, null, "process.launchUnconfirmed");
        }
        if (running != false || state.Identity is null)
        {
            replacements.Clear(definition.Id);
            return (state, running, running is null ? "process.observationUnavailable"
                                                    : null);
        }
        if (state.ExitedAt is not null)
        {
            return await ReadRecoveryAsync(definition, state, monitor, replacements, clock, preparingLaunch, cancellationToken).ConfigureAwait(false);
        }
        DateTimeOffset now = clock.GetUtcNow();
        if (!observed)
        {
            replacements.Clear(definition.Id);
            return (state with { ExitUnobserved = true, ExitedAt = now, ExitCounted = true, ExitCode = null, Ready = false, Healthy = false,
                NextRecoveryAt = null, Replacement = null, ReplacementProblem = null, RecoveryReplacement = null }, false, "process.exitUnobserved");
        }
        if (!state.PlannedStop && (state.Replacement is not null || 0 < definition.RestartDetection?.TimeoutMilliseconds))
        {
            ProcessReplacementWindow window = state.Replacement ?? new(now.AddMilliseconds(definition.RestartDetection!.TimeoutMilliseconds),
                monitor.LastObservedAlive(definition.Id) ?? state.Identity.StartedAt, definition.ExecutablePath, ContractJson.Hash(definition.Arguments), definition.RestartDetection.Match, monitor.Session(definition.Id));
            (ProcessIdentity? successor, string? problem) = await replacements.FindAsync(definition.Id, window, cancellationToken).ConfigureAwait(false);
            if (successor is not null && monitor.TryAdopt(definition.Id, successor))
            {
                replacements.Clear(definition.Id);
                return (Adopt(state, successor), true, "process.selfRestarted");
            }
            if (successor is not null)
            {
                problem = "process.replacementUnconfirmed";
            }
            now = clock.GetUtcNow();
            if (problem is not null || now < window.Deadline)
            {
                return (state with { Replacement = window, ReplacementProblem = problem, ExitCode = exitCode, Ready = false, Healthy = false }, false,
                    problem ?? "process.replacementWaiting");
            }
        }
        replacements.Clear(definition.Id);
        int failures = state.FailureCount;
        if (exitCode != 0 && !state.ExitCounted && !state.PlannedStop && !state.RecoverySuppressed)
        {
            failures = checked(failures + 1);
        }
        DateTimeOffset recoveryAt = now.AddMilliseconds(definition.Recovery?.DelayMilliseconds ?? options.RecoveryDelayMilliseconds);
        ProcessReplacementWindow? recovery = null;
        if (now < recoveryAt && !state.PlannedStop && !state.RecoverySuppressed && !state.RecoveryExhausted
            && RecoveryEnabled(definition.Recovery ?? new(), exitCode))
        {
            recovery = state.Replacement is { } confirmation ? confirmation with { Deadline = recoveryAt }
                                                             : RecoveryWindow(definition, state, monitor, recoveryAt);
        }
        return (state with { ExitedAt = now, ExitCode = exitCode, ExitCounted = true, FailureCount = failures, Ready = false, Healthy = false,
            NextRecoveryAt = recoveryAt, Replacement = null, ReplacementProblem = null, RecoveryReplacement = recovery }, false, null);
    }

    /// <summary>終了種別の選択を、復旧実行と待機中の後継確認で共用します。</summary>
    /// <returns>この終了を自動復旧の対象にするかです。</returns>
    internal static bool RecoveryEnabled(ProcessRecoveryPolicy policy, int? exitCode)
        => exitCode is null ? policy.RestartOnUnknownExit
                            : exitCode == 0 ? policy.RestartOnNormalExit
                                            : policy.RestartOnAbnormalExit;

    /// <summary>OSが起動を拒否した場合の次の待機にも、後継の照合条件を残します。</summary>
    /// <returns>起動前に保存する復旧意図です。</returns>
    internal static ProcessStateRecord RecoveryIntent(ProcessDefinition definition, ProcessStateRecord state, ProcessOptions options, ProcessHandleMonitor monitor, DateTimeOffset now)
    {
        DateTimeOffset next = now.AddMilliseconds(definition.Recovery?.DelayMilliseconds ?? options.RecoveryDelayMilliseconds);
        ProcessReplacementWindow? window = null;
        if (state.RecoveryReplacement is { } previous)
        {
            window = previous with { Deadline = next };
        }
        else if (now < next)
        {
            window = RecoveryWindow(definition, state, monitor, next);
        }
        return state with { PlannedStop = false, NextRecoveryAt = next, RecoveryReplacement = window };
    }

    /// <summary>復旧待ちの開始時点の実行物・引数・セッションを固定します。</summary>
    /// <returns>既存の復旧期限まで使う照合条件です。</returns>
    private static ProcessReplacementWindow RecoveryWindow(ProcessDefinition definition, ProcessStateRecord state, ProcessHandleMonitor monitor, DateTimeOffset deadline)
        => new(deadline, monitor.LastObservedAlive(definition.Id) ?? state.Identity!.StartedAt, definition.ExecutablePath,
            ContractJson.Hash(definition.Arguments), definition.RestartDetection?.Match ?? EProcessRestartMatch.ExecutableAndArguments, monitor.Session(definition.Id));

    /// <summary>終了確定時の条件で後継を探し、既存の復旧期限を延長しません。</summary>
    /// <returns>引継ぎ、照合待ち、または後継なしを確認した終了状態です。</returns>
    private static async ValueTask<(ProcessStateRecord State, bool? Running, string? Reason)> ReadRecoveryAsync(ProcessDefinition definition, ProcessStateRecord state,
        ProcessHandleMonitor monitor, ProcessReplacementProbe replacements, TimeProvider clock, bool preparingLaunch, CancellationToken cancellationToken = default)
    {
        if (state.RecoveryReplacement is not { } window || state.PlannedStop || state.RecoverySuppressed || state.RecoveryExhausted || state.ExitUnobserved
            || !RecoveryEnabled(definition.Recovery ?? new(), state.ExitCode))
        {
            replacements.Clear(definition.Id);
            return (state with { RecoveryReplacement = null, ReplacementProblem = null }, false, null);
        }
        if (!preparingLaunch && window.Deadline <= clock.GetUtcNow() && state.ReplacementProblem is null)
        {
            // 期限後は実際の復旧を始めるときに再確認します。Core再起動でも照合の起点を戻さないよう、条件は保持します。
            replacements.Clear(definition.Id);
            return (state, false, null);
        }
        (ProcessIdentity? successor, string? problem) = await replacements.FindAsync(definition.Id, window, cancellationToken).ConfigureAwait(false);
        if (successor is not null && monitor.TryAdopt(definition.Id, successor))
        {
            replacements.Clear(definition.Id);
            return (Adopt(state, successor), true, "process.selfRestarted");
        }
        if (successor is not null)
        {
            problem = "process.replacementUnconfirmed";
        }
        if (problem is not null || clock.GetUtcNow() < window.Deadline)
        {
            return (state with { ReplacementProblem = problem }, false, problem ?? "process.recoveryDelay");
        }
        replacements.Clear(definition.Id);
        return (state with { ReplacementProblem = null }, false, null);
    }

    /// <summary>一意に確認した実体へ監視を移し、前の起動回の終了状態を持ち越しません。</summary>
    /// <returns>後継を現起動回とした状態です。</returns>
    private static ProcessStateRecord Adopt(ProcessStateRecord state, ProcessIdentity successor)
        => state with { Identity = successor, InstanceId = Guid.NewGuid(), ExitCounted = false, ExitCode = null, ExitedAt = null,
            NextRecoveryAt = null, Ready = null, Healthy = null, RecoveryAttempts = 0, RecoveryExhausted = false,
            RestartRequested = true, ExitUnobserved = false, Replacement = null, ReplacementProblem = null, RecoveryReplacement = null };
}
