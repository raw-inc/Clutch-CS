using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>予定回を先に保存し、起動済み時の処理をアプリケーションの排他内で実行します。</summary>
internal sealed class ProcessScheduleRunner(ProcessScheduleStore store, TimeProvider clock, Func<SystemBootSession?> boot)
{
    /// <summary>暦の受付時刻またはOS起動回を確認し、未受付の一回だけを実行します。</summary>
    /// <returns>次回と直近の実行結果です。</returns>
    internal async ValueTask<ProcessScheduleStatus> RunAsync(string application, ProcessSchedule schedule, ProcessSession session,
        DateTimeOffset activatedAt, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        (SchedulePlan? calendar, string timeline) = ProcessSchedulePlan.Create(schedule, activatedAt);
        (string? version, ProcessScheduleCursor? current) = await store.ReadAsync(application, schedule.Id, cancellationToken).ConfigureAwait(false);
        if (ProcessSchedulePlan.IsBoot(schedule))
        {
            return await RunBootAsync(application, schedule, session, version, current, timeline, now, cancellationToken).ConfigureAwait(false);
        }
        SchedulePlan plan = calendar!;
        if (current is null || !string.Equals(current.Timeline, timeline, StringComparison.Ordinal))
        {
            EOutcome initialized = await store.SaveAsync(application, schedule.Id, version, new(timeline, now), cancellationToken).ConfigureAwait(false);
            return new(schedule.Id, plan.Next(now), Outcome: initialized, Reason: "process.scheduleInitialized");
        }
        DateTimeOffset? due = plan.Next(current.After);
        if (!schedule.Enabled || due is null || now < due)
        {
            return new(schedule.Id, schedule.Enabled ? due
                                                     : null, current.ScheduledAt, current.Outcome, current.Reason);
        }
        bool missed = due < activatedAt;
        ProcessScheduleCursor accepted = new(timeline, now, due, EOutcome.Unknown, "process.scheduleUnconfirmed");
        EOutcome claimed = await store.SaveAsync(application, schedule.Id, version, accepted, cancellationToken).ConfigureAwait(false);
        if (claimed != EOutcome.Succeeded)
        {
            return new(schedule.Id, due, current.ScheduledAt, claimed, "process.scheduleSaveUnconfirmed");
        }
        ProcessOperationResult result;
        if (missed)
        {
            result = new(EOutcome.NotExecuted, null, "process.scheduleMissed");
        }
        else
        {
            ProcessOperationResult observed = await session.ObserveAsync(cancellationToken).ConfigureAwait(false);
            result = await ExecuteAsync(schedule, session, observed, cancellationToken).ConfigureAwait(false);
        }
        (string? confirmed, _) = await store.ReadAsync(application, schedule.Id, CancellationToken.None).ConfigureAwait(false);
        EOutcome saved = await store.SaveAsync(application, schedule.Id, confirmed, accepted with { Outcome = result.Outcome, Reason = result.Reason }, CancellationToken.None).ConfigureAwait(false);
        return new(schedule.Id, plan.Next(now), due, saved == EOutcome.Succeeded ? result.Outcome
                                                                                 : EOutcome.Unknown,
            saved == EOutcome.Succeeded ? result.Reason
                                        : "process.scheduleResultUnconfirmed");
    }

    /// <summary>別のOS起動を一度だけ受け付け、Core起動の遅延やログイン待ちは同じ予定として扱います。</summary>
    /// <returns>一回の受付結果、または起動前の待機状態です。</returns>
    private async ValueTask<ProcessScheduleStatus> RunBootAsync(string application, ProcessSchedule schedule, ProcessSession session,
        string? version, ProcessScheduleCursor? current, string timeline, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        SystemBootSession? observation = boot();
        ProcessScheduleStatus status = ProcessSchedulePlan.BootStatus(schedule, current, observation, now);
        if (observation is not { } observedBoot || !schedule.Enabled)
        {
            return status;
        }
        if (current?.BootId is null || !string.Equals(current.Timeline, timeline, StringComparison.Ordinal))
        {
            ProcessScheduleCursor initialized = new(timeline, now, BootId: observedBoot.Id);
            EOutcome saved = await store.SaveAsync(application, schedule.Id, version, initialized, cancellationToken).ConfigureAwait(false);
            return saved == EOutcome.Succeeded ? ProcessSchedulePlan.BootStatus(schedule, initialized, observation, now)
                                               : status with { Outcome = saved, Reason = "process.scheduleSaveUnconfirmed" };
        }
        if (status.NextAt is not { } due || observedBoot.Uptime < TimeSpan.FromMilliseconds(schedule.BootDelayMilliseconds))
        {
            return status;
        }
        ProcessOperationResult observed = await session.ObserveAsync(cancellationToken).ConfigureAwait(false);
        bool needsLaunch = observed.Outcome == EOutcome.Succeeded && observed.Status?.Running is { } running
            && (!running || schedule.AlreadyRunning == EProcessRunningPolicy.Restart);
        bool userAvailable = !needsLaunch || session.IsLaunchUserAvailable();
        if (!userAvailable && !schedule.SkipWhenLoggedOut)
        {
            return status with { Outcome = null, Reason = ProcessSchedulePlan.AwaitingLogin };
        }
        ProcessScheduleCursor accepted = new(timeline, now, due, EOutcome.Unknown, "process.scheduleUnconfirmed", observedBoot.Id);
        EOutcome claimed = await store.SaveAsync(application, schedule.Id, version, accepted, cancellationToken).ConfigureAwait(false);
        if (claimed != EOutcome.Succeeded)
        {
            return status with { Outcome = claimed, Reason = "process.scheduleSaveUnconfirmed" };
        }
        ProcessOperationResult result = userAvailable ? await ExecuteAsync(schedule, session, observed, cancellationToken).ConfigureAwait(false)
                                                      : new(EOutcome.NotExecuted, observed.Status, "process.scheduleLoginUnavailable");
        string? reason = result.Reason;
        string userUnavailable = "process.interactiveUserUnavailable";
        if (!schedule.SkipWhenLoggedOut && result.Outcome == EOutcome.NotExecuted && string.Equals(reason, userUnavailable, StringComparison.Ordinal))
        {
            // 確認と起動の間のログアウトでは、OS未起動を確認できた場合に限り同じ回を待ち直します。
            reason = ProcessSchedulePlan.AwaitingLogin;
        }
        ProcessScheduleCursor completed = accepted with { Outcome = result.Outcome, Reason = reason };
        (string? confirmed, _) = await store.ReadAsync(application, schedule.Id, CancellationToken.None).ConfigureAwait(false);
        EOutcome persisted = await store.SaveAsync(application, schedule.Id, confirmed, completed, CancellationToken.None).ConfigureAwait(false);
        return ProcessSchedulePlan.BootStatus(schedule, completed, observation, now) with
        {
            Outcome = persisted == EOutcome.Succeeded ? result.Outcome
                                                      : EOutcome.Unknown,
            Reason = persisted == EOutcome.Succeeded ? reason
                                                     : "process.scheduleResultUnconfirmed",
        };
    }

    /// <summary>暦とPC起動の両予定で、同じ既起動・停止確認・起動の方針を使用します。</summary>
    /// <returns>副作用を実際に確認した結果です。</returns>
    private static async ValueTask<ProcessOperationResult> ExecuteAsync(ProcessSchedule schedule, ProcessSession session,
        ProcessOperationResult observed, CancellationToken cancellationToken = default)
    {
        if (observed.Outcome != EOutcome.Succeeded || observed.Status?.Running is null)
        {
            return new(EOutcome.Unknown, observed.Status, "process.previousStateUnconfirmed");
        }
        bool restart = observed.Status.Running == true;
        if (restart && schedule.AlreadyRunning == EProcessRunningPolicy.Skip)
        {
            return observed with { Reason = "process.scheduleAlreadyRunning" };
        }
        ProcessOperationResult result = observed;
        if (restart)
        {
            result = await session.StopAsync(schedule.StopPolicy!.Value, schedule.StopTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        return result.Outcome == EOutcome.Succeeded ? await session.StartAsync(restart, cancellationToken: cancellationToken).ConfigureAwait(false)
                                                    : result;
    }
}
