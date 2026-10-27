using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>生存・確認待ち・終了確定の遷移を計算し、OS実体の照合を一つに集約します。</summary>
internal static class ProcessObservation
{
    /// <summary>監視していない間の終了を再送せず、自律再起動の候補が一意なときだけ引き継ぎます。</summary>
    /// <returns>比較保存する次状態、生存観測と保留理由です。</returns>
    internal static async ValueTask<(ProcessStateRecord State, bool? Running, string? Reason)> ReadAsync(ProcessDefinition definition, ProcessStateRecord state,
        ProcessOptions options, ProcessHandleMonitor monitor, ProcessReplacementProbe replacements, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        (bool? running, int? exitCode, bool observed) = monitor.Observe(definition.Id, state.Identity);
        if (state.LaunchPending)
        {
            return (state, null, "process.launchUnconfirmed");
        }
        if (running != false || state.Identity is null || state.ExitedAt is not null)
        {
            replacements.Clear(definition.Id);
            return (state, running, running is null ? "process.observationUnavailable"
                                                    : null);
        }
        DateTimeOffset now = clock.GetUtcNow();
        if (!observed)
        {
            replacements.Clear(definition.Id);
            return (state with { ExitUnobserved = true, ExitedAt = now, ExitCounted = true, ExitCode = null, Ready = false, Healthy = false,
                NextRecoveryAt = null, Replacement = null, ReplacementProblem = null }, false, "process.exitUnobserved");
        }
        if (!state.PlannedStop && (state.Replacement is not null || 0 < definition.RestartDetection?.TimeoutMilliseconds))
        {
            ProcessReplacementWindow window = state.Replacement ?? new(now.AddMilliseconds(definition.RestartDetection!.TimeoutMilliseconds),
                monitor.LastObservedAlive(definition.Id) ?? state.Identity.StartedAt, definition.ExecutablePath, ContractJson.Hash(definition.Arguments), definition.RestartDetection.Match, monitor.Session(definition.Id));
            (ProcessIdentity? successor, string? problem) = await replacements.FindAsync(definition.Id, window, cancellationToken).ConfigureAwait(false);
            if (successor is not null && monitor.TryAdopt(definition.Id, successor))
            {
                replacements.Clear(definition.Id);
                return (state with { Identity = successor, InstanceId = Guid.NewGuid(), ExitCounted = false, ExitCode = null, ExitedAt = null,
                    NextRecoveryAt = null, Ready = null, Healthy = null, RecoveryAttempts = 0, RecoveryExhausted = false,
                    RestartRequested = true, ExitUnobserved = false, Replacement = null, ReplacementProblem = null }, true, "process.selfRestarted");
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
        return (state with { ExitedAt = now, ExitCode = exitCode, ExitCounted = true, FailureCount = failures, Ready = false, Healthy = false,
            NextRecoveryAt = now.AddMilliseconds(definition.Recovery?.DelayMilliseconds ?? options.RecoveryDelayMilliseconds),
            Replacement = null, ReplacementProblem = null }, false, null);
    }
}
