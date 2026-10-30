using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>保存した一回の計画を、停止確認・待機・順次起動の順で進めます。</summary>
internal sealed class ProcessGroupExecution(ProcessService processes, TimeProvider clock, string groupId)
{
    /// <summary>一段階を保存してから副作用を実行し、未確定な操作を自動で再送しません。</summary>
    /// <returns>次の巡回へ保存する操作状態です。</returns>
    internal async ValueTask<ProcessGroupRun?> StepAsync(ProcessGroupRun run, Func<ProcessGroupRun, CancellationToken, ValueTask<bool>> save,
        CancellationToken cancellationToken = default)
    {
        if (run.Phase is EProcessGroupPhase.Idle or EProcessGroupPhase.Paused)
        {
            return run;
        }
        if (run.Plan?.Members is null || run.Targets is null || run.Index < 0 || run.Targets.Count <= run.Index
            || !Enum.IsDefined(run.Phase) || !Enum.IsDefined(run.Command)
            || !string.Equals(run.Plan.Id, groupId, StringComparison.Ordinal)
            || run.Targets.Any(target => !run.Plan.Members.Any(member => string.Equals(member.ApplicationId, target.ApplicationId, StringComparison.Ordinal))))
        {
            return Pause(run, EOutcome.Unknown, "processGroup.invalidState");
        }
        if (run.Phase is EProcessGroupPhase.Waiting or EProcessGroupPhase.Starting)
        {
            for (int index = 0; index < run.Index; index++)
            {
                ProcessOperationResult predecessor = await processes.ObserveAsync(run.Targets[index].ApplicationId, cancellationToken).ConfigureAwait(false);
                if (predecessor.Outcome != EOutcome.Succeeded || predecessor.Status?.Running != true)
                {
                    return Pause(run, EOutcome.Unknown, "processGroup.predecessorUnavailable");
                }
            }
        }
        if (run.Phase == EProcessGroupPhase.Waiting)
        {
            if (clock.GetUtcNow() < run.NextAt)
            {
                return run;
            }
            if (run.Index == 0 && run.Command == EProcessGroupCommand.Restart)
            {
                foreach (ProcessGroupTarget member in run.Targets)
                {
                    ProcessOperationResult stopped = await processes.ObserveAsync(member.ApplicationId, cancellationToken).ConfigureAwait(false);
                    if (stopped.Outcome != EOutcome.Succeeded || stopped.Status?.Running != false)
                    {
                        return Pause(run, EOutcome.Unknown, "processGroup.stopNotConfirmed");
                    }
                }
            }
            run = run with { Phase = EProcessGroupPhase.Starting, NextAt = null };
        }
        ProcessGroupTarget target = run.Targets[run.Index];
        ProcessOperationResult observed = await processes.ObserveAsync(target.ApplicationId, cancellationToken).ConfigureAwait(false);
        bool stopping = run.Phase == EProcessGroupPhase.Stopping;
        if (run.Claimed)
        {
            // 中断した操作は実体で確認します。確認できなければ、OSへの依頼を繰り返しません。
            if (observed.Outcome == EOutcome.Succeeded && observed.Status?.Running == !stopping)
            {
                return Advance(run);
            }
            return Pause(run, EOutcome.Unknown, "processGroup.interruptedOperation");
        }
        if (observed.Outcome != EOutcome.Succeeded || observed.Status?.Running is null)
        {
            return Pause(run, EOutcome.Unknown, observed.Reason ?? "processGroup.observationUnavailable");
        }
        if (!stopping && observed.Status.Running == true && (!run.RecoverOnly || observed.Status.Healthy != false))
        {
            return Advance(run);
        }
        if (run.RecoverOnly && observed.Status.NextRecoveryAt is { } due && clock.GetUtcNow() < due)
        {
            return run with { Phase = EProcessGroupPhase.Waiting, NextAt = due };
        }
        ProcessGroupRun claimed = run with { Claimed = true };
        if (!await save(claimed, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        EProcessGroupCommand command = stopping ? EProcessGroupCommand.Stop
                                                : run.Command;
        ProcessOperationResult result = await processes.ExecuteGroupAsync(run.Plan.Id, target, command, run.Plan, run.Automatic,
            run.RecoverOnly, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != EOutcome.Succeeded || result.Status?.Running != !stopping)
        {
            return Pause(claimed, result.Outcome, result.Reason ?? "processGroup.operationUnconfirmed");
        }
        return Advance(claimed);
    }

    /// <summary>全員停止の境界を越えてから先頭へ戻り、以後は直前の起動確認から間隔を測ります。</summary>
    /// <returns>次の一段階または完了です。</returns>
    private ProcessGroupRun Advance(ProcessGroupRun run)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (run.Phase == EProcessGroupPhase.Stopping)
        {
            if (0 < run.Index)
            {
                return run with { Index = run.Index - 1, Claimed = false };
            }
            if (run.Command == EProcessGroupCommand.Stop)
            {
                return Finish(run);
            }
            DateTimeOffset start = run.StartNotBefore is { } earliest && now < earliest ? earliest
                                                                                        : now;
            return run with { Phase = EProcessGroupPhase.Waiting, Index = 0, Claimed = false, NextAt = start.AddMilliseconds(run.Targets![0].DelayMilliseconds) };
        }
        int index = run.Index + 1;
        if (index == run.Targets!.Count)
        {
            return Finish(run);
        }
        return run with { Phase = EProcessGroupPhase.Waiting, Index = index, Claimed = false, NextAt = now.AddMilliseconds(run.Targets[index].DelayMilliseconds) };
    }

    /// <summary>完了した操作の識別子と結果だけを残します。</summary>
    /// <returns>抑止状態を維持した待機状態です。</returns>
    private static ProcessGroupRun Finish(ProcessGroupRun run)
        => run with { Phase = EProcessGroupPhase.Idle, Plan = null, Targets = null, Index = 0, Claimed = false, NextAt = null, Outcome = EOutcome.Succeeded, Reason = null };

    /// <summary>不明な操作を止め、利用者の確認を待ちます。</summary>
    /// <returns>自動再試行しない状態です。</returns>
    internal static ProcessGroupRun Pause(ProcessGroupRun run, EOutcome outcome, string reason)
        => run with { Phase = EProcessGroupPhase.Paused, NextAt = null, Outcome = outcome, Reason = reason };
}
