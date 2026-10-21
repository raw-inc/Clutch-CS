using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>電源予定をクラスタの共有設定として保存し、変更した本文だけを再構築します。</summary>
public sealed class HostPowerScheduleStore(PeerCluster cluster, IDurableRepository repository, TimeProvider? timeProvider = null)
{
    internal static string Scope => "host:power-schedules";
    internal static string ActiveScope => "host:power-schedule-activation";
    internal static string Occurrences => "host:power-schedule-occurrences";
    internal static string WakeProfiles => "host:power-wake";

    /// <summary>停止中の相手へ新たな共有保存を要求せず、処理担当が結果を保持します。</summary>
    /// <returns>担当Peerだけが書く最新結果の保存範囲です。</returns>
    internal static string ResultsScope(string peerId) => "peer:" + peerId + ":power-schedule-results";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, string> known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HostPowerScheduleRecord Record, string Hash)> cached = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> knownActivation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostPowerScheduleActivation> active = new(StringComparer.Ordinal);

    /// <summary>操作結果は維持し、表示済み版が一致する予定だけを置き換えます。</summary>
    /// <returns>共有保存の成否と読取り可能な設定です。</returns>
    public async Task<HostPowerScheduleSaveResult> SaveAsync(HostPowerScheduleSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            ValidateTarget(request.ClusterId, request.HostId);
            ValidateSchedules(request.Schedules, clock.GetUtcNow());
            if (request.WakeProfile is { } profile)
            {
                ArgumentNullException.ThrowIfNull(profile.State);
                profile.State.Validate();
                if (!string.Equals(profile.HostId, request.HostId, StringComparison.Ordinal) || profile.Clusters is null
                    || !profile.Clusters.Contains(cluster.LocalClusterId, StringComparer.Ordinal)
                    || !string.Equals(DeviceSnapshots.Create(profile.State).Revision, profile.Revision, StringComparison.Ordinal))
                {
                    throw new ArgumentException("powerSchedule.invalid", nameof(request));
                }
                foreach (Contracts.DeviceManagement.WakeOnLan.DeviceWakeInterface adapter in profile.State.Selected())
                {
                    _ = Processes.WakeOnLanSender.Packet(adapter.MacAddress);
                }
            }
            if (request.OperationId == Guid.Empty)
            {
                throw new ArgumentException("powerSchedule.invalid", nameof(request));
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new(EOutcome.NotExecuted, "powerSchedule.invalid");
        }
        DurableEntry? current = await repository.ReadAsync(Scope, request.HostId, cancellationToken).ConfigureAwait(false);
        DurableEntry? wake = await repository.ReadAsync(WakeProfiles, request.HostId, cancellationToken).ConfigureAwait(false);
        if (current is null && wake?.Value is null && request.WakeProfile is null && !cluster.GetPeers().Any(peer => string.Equals(peer.HostId, request.HostId, StringComparison.Ordinal)))
        {
            return new(EOutcome.NotExecuted, "powerSchedule.hostUnknown");
        }
        PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        HostPowerScheduleRecord record = new(request.HostId, request.Schedules, clock.GetUtcNow(), request.OperationId, members);
        List<DurableMutation> changes = [new(Scope, request.HostId, request.ExpectedRevision, NodeValue.FromObject(record))];
        if (wake?.Value is null && request.WakeProfile is { } initial)
        {
            // Coreが既に保持する新しい選択を上書きせず、停止前にClientHostが確認した一覧だけを初回の足掛かりにします。
            changes.Add(new(WakeProfiles, request.HostId, wake?.Version, NodeValue.FromObject(new HostPowerWakeProfile(request.HostId, initial.State))));
        }
        if (current?.Value is not null)
        {
            HostPowerScheduleRecord old = Decode(current) ?? throw new InvalidDataException("電源予定の保存形式が不正です。");
            foreach (HostPowerSchedule removed in old.Schedules.Where(item => !request.Schedules.Any(next => next.Id == item.Id)))
            {
                string key = OccurrenceKey(request.HostId, removed.Id);
                DurableEntry? cursor = await repository.ReadAsync(Occurrences, key, cancellationToken).ConfigureAwait(false);
                if (cursor?.Value is not null)
                {
                    changes.Add(new(Occurrences, key, cursor.Version, null));
                }
            }
        }
        DurableReceipt saved = await cluster.ReplicateFixedAsync(request.OperationId, changes, members, cancellationToken).ConfigureAwait(false);
        if (saved.Outcome != EOutcome.Succeeded)
        {
            return new(saved.Outcome, saved.Reason ?? "powerSchedule.saveUnconfirmed");
        }
        DurableEntry? activation = await repository.ReadAsync(ActiveScope, request.HostId, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleActivation confirmed = new(record.OperationId, ContractJson.Hash(NodeValue.FromObject(record)));
        DurableReceipt activated = await cluster.ReplicateAsync(Guid.CreateVersion7(),
            [new(ActiveScope, request.HostId, activation?.Version, NodeValue.FromObject(confirmed))], cancellationToken).ConfigureAwait(false);
        if (activated.Outcome != EOutcome.Succeeded)
        {
            return new(activated.Outcome, "powerSchedule.activationUnconfirmed");
        }
        return new(saved.Outcome, null, await ReadAsync(new(request.ClusterId, request.HostId), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>停止中の対象Hostも、同じクラスタの稼働中Coreから予定を参照できます。</summary>
    /// <returns>予定と一件ずつの直近結果です。</returns>
    public async Task<HostPowerScheduleView> ReadAsync(HostPowerScheduleReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTarget(request.ClusterId, request.HostId);
        DurableEntry? entry = await repository.ReadAsync(Scope, request.HostId, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleRecord? record = entry?.Value is null ? null
                                                               : Decode(entry) ?? throw new InvalidDataException("電源予定の保存形式が不正です。");
        List<HostPowerScheduleStatus> statuses = [];
        foreach (HostPowerSchedule schedule in record?.Schedules ?? [])
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            DurableEntry? saved = await repository.ReadAsync(Occurrences, OccurrenceKey(request.HostId, schedule.Id), cancellationToken).ConfigureAwait(false);
            HostPowerScheduleOccurrence? last = saved?.Value?.Json.Deserialize<HostPowerScheduleOccurrence>();
            DurableEntry? local = await repository.ReadAsync(ResultsScope(cluster.LocalPeerId), OccurrenceKey(request.HostId, schedule.Id), cancellationToken).ConfigureAwait(false);
            HostPowerScheduleOccurrence? completed = local?.Value?.Json.Deserialize<HostPowerScheduleOccurrence>();
            if (last is not null && completed?.OperationId == last.OperationId)
            {
                last = completed;
            }
            DateTimeOffset? next = null;
            if (schedule.Enabled)
            {
                next = HostPowerSchedulePlan.Create(schedule, record!.ChangedAt).Next(clock.GetUtcNow());
                if (next is { } due)
                {
                    next = TimeZoneInfo.ConvertTime(due, zone);
                }
            }
            DateTimeOffset? lastAt = null;
            if (last is not null)
            {
                lastAt = TimeZoneInfo.ConvertTime(last.DueAt, zone);
            }
            statuses.Add(new(schedule.Id, next, lastAt, last?.Outcome, last?.Reason));
        }
        bool wake = (await repository.ReadAsync(WakeProfiles, request.HostId, cancellationToken).ConfigureAwait(false))?.Value is not null;
        DurableEntry? activation = await repository.ReadAsync(ActiveScope, request.HostId, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleActivation? confirmed = activation?.Value?.Json.Deserialize<HostPowerScheduleActivation>();
        bool activated = record is not null && confirmed?.OperationId == record.OperationId && string.Equals(confirmed.ValueHash, ContractJson.Hash(entry!.Value), StringComparison.Ordinal);
        activated &= !(await repository.GetConflictsAsync(Scope, cancellationToken).ConfigureAwait(false)).Any(item => string.Equals(item.Key, request.HostId, StringComparison.Ordinal))
            && !(await repository.GetConflictsAsync(ActiveScope, cancellationToken).ConfigureAwait(false)).Any(item => string.Equals(item.Key, request.HostId, StringComparison.Ordinal));
        return new(request.ClusterId, request.HostId, entry?.Version, record?.Schedules ?? [], statuses, wake, activated);
    }

    /// <summary>定期読取りは索引と既知の指紋で済ませ、変更・未確認の設定だけを処理します。</summary>
    /// <returns>元参加者への共有を確認できた現在の設定です。</returns>
    internal async Task<IReadOnlyList<HostPowerScheduleRecord>> ReadConfirmedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableObservation> observed = await repository.ObserveAsync(Scope, known, cancellationToken: cancellationToken).ConfigureAwait(false);
        HashSet<string> present = observed.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in known.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            known.Remove(removed);
            cached.Remove(removed);
        }
        foreach (DurableObservation item in observed)
        {
            known[item.Key] = item.Fingerprint;
            if (item.Entry is { } entry)
            {
                cached.Remove(item.Key);
                if (entry.Value is not null && Decode(entry) is { } record)
                {
                    cached[item.Key] = (record, ContractJson.Hash(entry.Value));
                }
            }
        }
        IReadOnlyList<DurableObservation> activations = await repository.ObserveAsync(ActiveScope, knownActivation, cancellationToken: cancellationToken).ConfigureAwait(false);
        HashSet<string> activationKeys = activations.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in knownActivation.Keys.Where(key => !activationKeys.Contains(key)).ToArray())
        {
            knownActivation.Remove(removed);
            active.Remove(removed);
        }
        foreach (DurableObservation observation in activations)
        {
            knownActivation[observation.Key] = observation.Fingerprint;
            if (observation.Entry is { } entry)
            {
                active.Remove(observation.Key);
                try
                {
                    if (entry.Value?.Json.Deserialize<HostPowerScheduleActivation>() is { } value)
                    {
                        active[observation.Key] = value;
                    }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                {
                    // このHostの不完全な稼働基準は使わず、他の予定の観測を継続します。
                }
            }
        }
        HashSet<string> conflicts = (await repository.GetConflictsAsync(Scope, cancellationToken).ConfigureAwait(false)).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        conflicts.UnionWith((await repository.GetConflictsAsync(ActiveScope, cancellationToken).ConfigureAwait(false)).Select(entry => entry.Key));
        List<HostPowerScheduleRecord> result = [];
        foreach ((string key, (HostPowerScheduleRecord record, string hash)) in cached)
        {
            if (conflicts.Contains(key))
            {
                continue;
            }
            if (!active.TryGetValue(key, out HostPowerScheduleActivation? activation) || activation.OperationId != record.OperationId
                || !string.Equals(activation.ValueHash, hash, StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(record);
        }
        return result;
    }

    /// <summary>区切り文字を含むHost名でも、別の予定と衝突させません。</summary>
    /// <returns>予定ごとに固定した現在位置のキーです。</returns>
    internal static string OccurrenceKey(string hostId, Guid scheduleId) => ContractJson.Hash(new { hostId, scheduleId });

    /// <summary>外側のクラスタ指定とHost識別を省略させません。</summary>
    private void ValidateTarget(string clusterId, string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        if (120 < hostId.Length || !string.Equals(cluster.LocalClusterId, clusterId, StringComparison.Ordinal))
        {
            throw new ArgumentException("powerSchedule.invalid", nameof(hostId));
        }
    }

    /// <summary>構造と暦を実行前に検査します。</summary>
    private static void ValidateSchedules(IReadOnlyList<HostPowerSchedule> schedules, DateTimeOffset activatedAt)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        if (32 < schedules.Count || schedules.Any(item => item is null) || schedules.Select(item => item.Id).Distinct().Count() != schedules.Count)
        {
            throw new ArgumentException("powerSchedule.invalid", nameof(schedules));
        }
        foreach (HostPowerSchedule schedule in schedules)
        {
            _ = HostPowerSchedulePlan.Create(schedule, activatedAt);
        }
    }

    /// <summary>保存された不完全な設定を既定値による自動電源操作へ変換しません。</summary>
    /// <returns>共有証拠を持つ保存定義です。不正な一Hostの予定はnullとして他Hostの監視を維持します。</returns>
    private static HostPowerScheduleRecord? Decode(DurableEntry entry)
    {
        try
        {
            HostPowerScheduleRecord? record = entry.Value?.Json.Deserialize<HostPowerScheduleRecord>();
            if (record is null || !string.Equals(entry.Key, record.HostId, StringComparison.Ordinal) || record.OperationId == Guid.Empty || record.ChangedAt == default
                || record.Members is null || record.Members.Count == 0 || record.Members.Any(peer => peer is null || peer.InstanceId == Guid.Empty))
            {
                return null;
            }
            ValidateSchedules(record.Schedules, record.ChangedAt);
            return record;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException or InvalidOperationException)
        {
            return null;
        }
    }
}
