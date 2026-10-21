using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Scheduling;

/// <summary>予約の開始意図を先に保存し、不明・再起動後は元IDの照会だけを行います。</summary>
public sealed class UpdateScheduler(UpdateScheduleStore store, IUpdateScheduleLauncher launcher, Func<bool> canStart, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>予約状態を読めない等の運用診断です。</summary>
    public string? Problem
    {
        get; private set;
    }

    /// <summary>Coreの寿命中だけ予約を監視し、実Updaterの寿命とは分けます。</summary>
    /// <returns>監視停止までの処理です。</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), clock);
        do
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
                Problem = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Problem = "schedule.stateUnavailable";
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>同時の取消・変更とCASを競合させ、今回開始を所有した予約だけ起動します。</summary>
    /// <returns>一回の期限確認と結果照会です。</returns>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UpdateScheduleView> views = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        bool active = views.Any(item => item.Schedule.State is EUpdateScheduleState.Starting or EUpdateScheduleState.Running or EUpdateScheduleState.Unconfirmed);
        foreach (UpdateScheduleView view in views.Where(item => item.Schedule.State is EUpdateScheduleState.Waiting or EUpdateScheduleState.Starting or EUpdateScheduleState.Running or EUpdateScheduleState.Unconfirmed).Take(128))
        {
            ScheduledUpdate schedule = view.Schedule;
            if (schedule.State != EUpdateScheduleState.Waiting)
            {
                await ObserveAsync(view, cancellationToken).ConfigureAwait(false);
                continue;
            }
            DateTimeOffset now = clock.GetUtcNow();
            if (now < schedule.Plan.StartAt)
            {
                continue;
            }
            if (schedule.Plan.StartAt.AddSeconds(schedule.Plan.MaximumDelaySeconds) < now)
            {
                await store.WriteAsync(view.Version, schedule with { State = EUpdateScheduleState.Expired, Outcome = EOutcome.NotExecuted, Reason = "schedule.latenessExceeded" }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (active || !canStart())
            {
                continue;
            }
            UpdateScheduleResult claim = await store.WriteAsync(view.Version, schedule with { State = EUpdateScheduleState.Starting, Outcome = EOutcome.Unknown, Reason = "schedule.startReserved" }, cancellationToken).ConfigureAwait(false);
            if (claim.Outcome != EOutcome.Succeeded || claim.View is null)
            {
                continue;
            }
            ScheduledUpdate next;
            try
            {
                ClusterUpdateStatus update = await launcher.StartScheduledAsync(schedule.Plan.Update, schedule.PeerInstances, cancellationToken).ConfigureAwait(false);
                EUpdateScheduleState state = EUpdateScheduleState.Running;
                if (update.Completed)
                {
                    state = EUpdateScheduleState.Completed;
                }
                next = schedule with { State = state, Outcome = update.Outcome, Update = update, Reason = update.Reason };
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                next = schedule with { State = EUpdateScheduleState.Unconfirmed, Outcome = EOutcome.Unknown, Reason = "schedule.startUnconfirmedNoReplay" };
            }
            await store.WriteAsync(claim.View.Version, next, cancellationToken).ConfigureAwait(false);
            active = true;
        }
    }

    /// <summary>開始意図しか残らない再起動でも、同じ更新を起動し直しません。</summary>
    /// <returns>元IDを照会した状態だけを反映します。</returns>
    private async Task ObserveAsync(UpdateScheduleView view, CancellationToken cancellationToken = default)
    {
        ScheduledUpdate schedule = view.Schedule;
        ClusterUpdateStatus? update = await launcher.ReadAsync(schedule.Plan.Update.OperationId, cancellationToken).ConfigureAwait(false);
        ScheduledUpdate next = schedule with { State = EUpdateScheduleState.Unconfirmed, Outcome = EOutcome.Unknown, Reason = "schedule.awaitOriginalStatusNoReplay" };
        if (update is not null)
        {
            if (!string.Equals(ContractJson.Hash(update.Request), ContractJson.Hash(schedule.Plan.Update), StringComparison.Ordinal))
            {
                next = next with { Reason = "schedule.originalRequestMismatch" };
            }
            else
            {
                EUpdateScheduleState state = EUpdateScheduleState.Running;
                if (update.Completed)
                {
                    state = EUpdateScheduleState.Completed;
                }
                next = schedule with { State = state, Outcome = update.Outcome, Update = update, Reason = update.Reason };
            }
        }
        if (!string.Equals(ContractJson.Hash(schedule), ContractJson.Hash(next), StringComparison.Ordinal))
        {
            await store.WriteAsync(view.Version, next, cancellationToken).ConfigureAwait(false);
        }
    }
}
