using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>ブラウザーから独立して時刻を監視し、同クラスタのCoordinatorから電源予定を受け付けます。</summary>
public sealed class HostPowerScheduler
{
    private readonly HostPowerScheduleStore store;
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly string hostId;
    private readonly Func<CancellationToken, Task<DeviceWakeState>> readWake;
    private readonly IWorkflowLog log;
    private readonly TimeProvider clock;
    private readonly HostPowerScheduleOperations operations;
    private readonly List<HostPowerScheduleRecord> settings = [];
    private readonly Dictionary<(string Host, Guid Schedule), (Guid Configuration, SchedulePlan Plan)> plans = [];
    private DateTimeOffset after;
    private DateTimeOffset refreshAt;
    private DateTimeOffset learnAt;

    /// <summary>既存の保存、管理要求、NIC設定へ接続します。</summary>
    public HostPowerScheduler(HostPowerScheduleStore store, PeerCluster cluster, IDurableRepository repository, string hostId,
        Func<CancellationToken, Task<DeviceWakeState>> readWake, IWorkflowLog log, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.store = store;
        this.cluster = cluster;
        this.repository = repository;
        this.hostId = hostId;
        this.readWake = readWake;
        this.log = log;
        clock = timeProvider ?? TimeProvider.System;
        operations = new(cluster, repository, clock);
    }

    /// <summary>起動前や長期中断中の予定を遡らず、今回到来した回だけを有限時間で処理します。</summary>
    /// <returns>設定観測、NIC保持と今回の受付の完了です。</returns>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow(), previous = after;
        after = now;
        if (previous == default)
        {
            previous = now;
        }
        if (refreshAt <= now)
        {
            IReadOnlyList<HostPowerScheduleRecord> current = await store.ReadConfirmedAsync(cancellationToken).ConfigureAwait(false);
            settings.Clear();
            settings.AddRange(current);
            RefreshPlans();
            refreshAt = clock.GetUtcNow().AddSeconds(5);
        }
        PeerInfo? local = cluster.GetPeers().FirstOrDefault(peer => peer.InstanceId == cluster.LocalInstanceId);
        if (local is null || !PeerStatusRules.CanParticipate(local.Status))
        {
            return;
        }
        if (learnAt <= now && settings.Any(item => string.Equals(item.HostId, hostId, StringComparison.Ordinal)
            && item.Schedules.Any(schedule => schedule.Enabled && schedule.Action == EHostPowerScheduleAction.Wake)))
        {
            learnAt = now.AddSeconds(30);
            try
            {
                using CancellationTokenSource learning = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                learning.CancelAfter(TimeSpan.FromSeconds(10));
                await LearnAsync(learning.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (Expected(error) && !cancellationToken.IsCancellationRequested)
            {
                await log.WriteAsync(new(Guid.NewGuid(), clock.GetUtcNow(), "WARN", cluster.LocalPeerId, "Host.PowerSchedule.WakeProfileUnconfirmed",
                    Data: NodeValue.FromObject(new { HostId = hostId, ExceptionType = error.GetType().FullName })), cancellationToken).ConfigureAwait(false);
            }
        }
        if (!string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return;
        }
        List<(HostPowerScheduleRecord Host, HostPowerSchedule Schedule, DateTimeOffset Due)> pending = [];
        foreach (HostPowerScheduleRecord host in settings)
        {
            foreach (HostPowerSchedule schedule in host.Schedules)
            {
                SchedulePlan plan = plans[(host.HostId, schedule.Id)].Plan;
                if (HostPowerSchedulePlan.Due(plan, schedule.Enabled, host.ChangedAt, previous, now) is { } due)
                {
                    pending.Add((host, schedule, due));
                }
            }
        }
        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            List<(HostPowerScheduleRecord Host, HostPowerSchedule Schedule, HostPowerScheduleOccurrence Occurrence)> accepted = [];

            // 同時刻の送信権は、どのPCも停止させる前に確定します。停止開始後のPeerへ新しい準備確認を要求しません。
            foreach ((HostPowerScheduleRecord host, HostPowerSchedule schedule, DateTimeOffset due) in pending)
            {
                using CancellationTokenSource claiming = CancellationTokenSource.CreateLinkedTokenSource(window.Token);
                claiming.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    if (await operations.ClaimAsync(host, schedule.Id, due, claiming.Token).ConfigureAwait(false) is { } occurrence)
                    {
                        accepted.Add((host, schedule, occurrence));
                    }
                }
                catch (Exception error) when (Expected(error) && !window.IsCancellationRequested)
                {
                    await log.WriteAsync(new(Guid.NewGuid(), clock.GetUtcNow(), "WARN", cluster.LocalPeerId, "Host.PowerSchedule.ClaimUnconfirmed",
                        Data: NodeValue.FromObject(new { host.HostId, schedule.Id, ScheduledAt = due, ExceptionType = error.GetType().FullName })), window.Token).ConfigureAwait(false);
                }
            }

            // 他Hostへの要求を先に終え、送信元PC自身の停止で後続の送信を打ち切らない順序にします。
            await Parallel.ForEachAsync(accepted.Where(item => item.Schedule.Action != EHostPowerScheduleAction.Shutdown || !string.Equals(item.Host.HostId, hostId, StringComparison.Ordinal)),
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = window.Token },
                async (item, token) => await RunAsync(item.Host, item.Schedule, item.Occurrence, token).ConfigureAwait(false)).ConfigureAwait(false);
            foreach ((HostPowerScheduleRecord host, HostPowerSchedule schedule, HostPowerScheduleOccurrence occurrence) in accepted.Where(item => item.Schedule.Action == EHostPowerScheduleAction.Shutdown && string.Equals(item.Host.HostId, hostId, StringComparison.Ordinal)))
            {
                await RunAsync(host, schedule, occurrence, window.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // この観測窓に開始できなかった予定は、次回起動・次回巡回に持ち越しません。
        }
    }

    /// <summary>設定が変わった予定だけを再解析し、削除済みの予定を保持しません。</summary>
    private void RefreshPlans()
    {
        HashSet<(string Host, Guid Schedule)> present = [];
        foreach (HostPowerScheduleRecord host in settings)
        {
            foreach (HostPowerSchedule schedule in host.Schedules)
            {
                (string Host, Guid Schedule) key = (host.HostId, schedule.Id);
                present.Add(key);
                if (!plans.TryGetValue(key, out (Guid Configuration, SchedulePlan Plan) existing) || existing.Configuration != host.OperationId)
                {
                    plans[key] = (host.OperationId, HostPowerSchedulePlan.Create(schedule, host.ChangedAt));
                }
            }
        }
        foreach ((string Host, Guid Schedule) removed in plans.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            plans.Remove(removed);
        }
    }

    /// <summary>NIC選択の変更時だけ共有し、電源OFF後も最後に確認したMACを維持します。</summary>
    /// <returns>内容が変わったプロフィールの保存です。</returns>
    private async Task LearnAsync(CancellationToken cancellationToken = default)
    {
        DeviceWakeState state = await readWake(cancellationToken).ConfigureAwait(false);
        state.Validate();
        NodeValue value = NodeValue.FromObject(new HostPowerWakeProfile(hostId, state));
        DurableEntry? previous = await repository.ReadAsync(HostPowerScheduleStore.WakeProfiles, hostId, cancellationToken).ConfigureAwait(false);
        if (previous?.Value is not null && string.Equals(ContractJson.Hash(previous.Value), ContractJson.Hash(value), StringComparison.Ordinal))
        {
            return;
        }
        await cluster.ReplicateAsync(Guid.CreateVersion7(), [new(HostPowerScheduleStore.WakeProfiles, hostId, previous?.Version, value)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一回の障害を他Hostの予定へ波及させず、送信済みの再試行はしません。</summary>
    /// <returns>今回の送信と結果記録です。</returns>
    private async Task RunAsync(HostPowerScheduleRecord host, HostPowerSchedule schedule, HostPowerScheduleOccurrence accepted, CancellationToken cancellationToken = default)
    {
        NodeResult result;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            result = await operations.DispatchAsync(host.HostId, schedule, accepted, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (Expected(error))
        {
            result = NodeResult.Failure("powerSchedule.unconfirmed", error.GetType().Name, EOutcome.Unknown);
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource saving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                saving.CancelAfter(TimeSpan.FromSeconds(5));
                await operations.CompleteAsync(host.HostId, accepted, result, saving.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (Expected(error))
            {
                result = result with { Outcome = EOutcome.Unknown, ReasonCode = "powerSchedule.resultUnconfirmed" };
            }
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            string level = result.Outcome is EOutcome.Failed or EOutcome.Unknown ? "WARN"
                                                                                 : "INFO";
            await log.WriteAsync(new(Guid.NewGuid(), clock.GetUtcNow(), level, cluster.LocalPeerId, "Host.PowerSchedule",
                CausationId: accepted.OperationId, Data: NodeValue.FromObject(new { host.HostId, schedule.Id, schedule.Action, ScheduledAt = accepted.DueAt, result.Outcome, result.ReasonCode })), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>通信・保存・入力の不成立を、他Hostの予定へ広げません。</summary>
    /// <returns>予定ごとに記録して隔離する障害かです。</returns>
    private static bool Expected(Exception error) => error is IOException or System.Data.Common.DbException or JsonException
        or InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException or OperationCanceledException;
}
