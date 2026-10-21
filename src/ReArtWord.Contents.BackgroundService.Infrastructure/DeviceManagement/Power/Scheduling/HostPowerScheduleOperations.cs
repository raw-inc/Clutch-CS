using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>一回の電源予定を先に共有し、元実体への送信と元結果の保存だけを行います。</summary>
internal sealed class HostPowerScheduleOperations(PeerCluster cluster, IDurableRepository repository, TimeProvider clock)
{
    /// <summary>予定ごとに一行だけ進め、未確認の回を別Peerから送り直しません。</summary>
    /// <returns>今回取得した送信権、または既受理・未確認ならnullです。</returns>
    internal async Task<HostPowerScheduleOccurrence?> ClaimAsync(HostPowerScheduleRecord configuration, Guid scheduleId, DateTimeOffset due, CancellationToken cancellationToken = default)
    {
        string hostId = configuration.HostId;
        DurableEntry? definition = await repository.ReadAsync(HostPowerScheduleStore.Scope, hostId, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleRecord? currentDefinition = definition?.Value?.Json.Deserialize<HostPowerScheduleRecord>();
        DurableEntry? active = await repository.ReadAsync(HostPowerScheduleStore.ActiveScope, hostId, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleActivation? activation = active?.Value?.Json.Deserialize<HostPowerScheduleActivation>();
        if (currentDefinition?.OperationId != configuration.OperationId
            || activation?.OperationId != configuration.OperationId || !string.Equals(activation.ValueHash, ContractJson.Hash(definition?.Value), StringComparison.Ordinal)
            || (await repository.GetConflictsAsync(HostPowerScheduleStore.ActiveScope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, hostId, StringComparison.Ordinal))
            || (await repository.GetConflictsAsync(HostPowerScheduleStore.Scope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, hostId, StringComparison.Ordinal)))
        {
            return null;
        }
        string key = HostPowerScheduleStore.OccurrenceKey(hostId, scheduleId);
        DurableEntry? current = await repository.ReadAsync(HostPowerScheduleStore.Occurrences, key, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleOccurrence? previous = current?.Value?.Json.Deserialize<HostPowerScheduleOccurrence>();
        if (previous is not null && due <= previous.DueAt)
        {
            return null;
        }
        HostPowerScheduleOccurrence accepted = new(scheduleId, due, Guid.CreateVersion7(), EOutcome.Unknown, "powerSchedule.unconfirmed");
        DurableReceipt result = await cluster.ReplicateAsync(accepted.OperationId,
            [new(HostPowerScheduleStore.Occurrences, key, current?.Version, NodeValue.FromObject(accepted))], cancellationToken).ConfigureAwait(false);
        return result.Outcome == EOutcome.Succeeded ? accepted
                                                    : null;
    }

    /// <summary>起動対象の生存を送信直前にも確認し、同じクラスタのCoreだけへ配送します。</summary>
    /// <returns>パケットまたはOS要求の受付結果です。</returns>
    internal async Task<NodeResult> DispatchAsync(string hostId, HostPowerSchedule schedule, HostPowerScheduleOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        DateTimeOffset expires = occurrence.DueAt.AddMinutes(1);
        DateTimeOffset now = clock.GetUtcNow();
        if (expires <= now)
        {
            return NodeResult.Failure("powerSchedule.missed", "The scheduled time is no longer current.", EOutcome.NotExecuted);
        }
        PeerInfo[] peers = cluster.GetPeers().Where(peer => now - peer.LastSeen <= TimeSpan.FromSeconds(15)).ToArray();
        PeerInfo? target;
        object payload;
        string operation;
        if (schedule.Action == EHostPowerScheduleAction.Wake)
        {
            if (peers.Any(peer => string.Equals(peer.HostId, hostId, StringComparison.Ordinal) && peer.Status != EPeerStatus.Unavailable))
            {
                return NodeResult.Failure("wake.alreadyOnline", "The target host is already online.", EOutcome.NotExecuted);
            }
            DurableEntry? saved = await repository.ReadAsync(HostPowerScheduleStore.WakeProfiles, hostId, cancellationToken).ConfigureAwait(false);
            HostPowerWakeProfile? profile = saved?.Value?.Json.Deserialize<HostPowerWakeProfile>();
            if (profile is null || !string.Equals(profile.HostId, hostId, StringComparison.Ordinal))
            {
                return NodeResult.Failure("wake.notLearned", "No confirmed wake interfaces have been retained for this host.", EOutcome.NotExecuted);
            }
            DeviceWakeState state = profile.State;
            state.Validate();
            string[] macs = state.Selected().Select(item => item.MacAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0)
            {
                return NodeResult.Failure("wake.noInterfaces", "No wake interfaces are selected.", EOutcome.NotExecuted);
            }
            target = peers.Where(peer => PeerStatusRules.CanParticipate(peer.Status) && !string.Equals(peer.HostId, hostId, StringComparison.Ordinal))
                .OrderByDescending(peer => state.Inventory.Addresses.Count(network => WakeOnLanRoute.Contains(peer.Address, network)))
                .ThenBy(peer => peer.PeerId, StringComparer.Ordinal).FirstOrDefault();
            operation = "host.wake";
            payload = new HostWakeRequest(occurrence.OperationId, hostId, macs);
        }
        else
        {
            target = peers.Where(peer => string.Equals(peer.HostId, hostId, StringComparison.Ordinal) && peer.Status is EPeerStatus.Ready or EPeerStatus.Syncing)
                .OrderBy(peer => peer.PeerId, StringComparer.Ordinal).FirstOrDefault();
            operation = "host.power";
            payload = new HostPowerRequest(occurrence.OperationId, hostId, target?.InstanceId ?? Guid.Empty, EHostPowerAction.Shutdown,
                NotAfter: expires, ScheduledAt: occurrence.DueAt);
        }
        if (target is null)
        {
            return NodeResult.Failure("powerSchedule.noTarget", "No eligible Core is available in this cluster.", EOutcome.NotExecuted);
        }
        ManagementCommand command = new(occurrence.OperationId, cluster.LocalClusterId, target.PeerId, target.InstanceId, expires, operation, ContractJson.ToValue(payload));
        ClusterRequest request = new(command.RequestId, ManagementWire.ExecuteKind, command.TargetPeerId, command.TargetInstanceId, command.ExpiresAt, ContractJson.ToValue(command));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        ManagementReply? reply = response.Value?.Json.Deserialize<ManagementReply>(ContractJson.Options);
        if (response.Outcome != EOutcome.Succeeded || reply?.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            EOutcome outcome = reply?.Outcome ?? response.Outcome;
            if (outcome == EOutcome.Succeeded)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure(reply?.Reason ?? response.Reason ?? "powerSchedule.unconfirmed", "The original power request was not confirmed.",
                outcome);
        }
        if (schedule.Action == EHostPowerScheduleAction.Wake)
        {
            HostWakeResult result = reply.Value.Json.Deserialize<HostWakeResult>(ContractJson.Options) ?? throw new InvalidDataException("WoL応答がありません。");
            if (result.OperationId != occurrence.OperationId)
            {
                throw new InvalidDataException("WoL応答の操作が一致しません。");
            }
            return result.Outcome == EOutcome.Succeeded ? NodeResult.Success() with { ReasonCode = result.Reason }
                                                        : NodeResult.Failure(result.Reason ?? "wake.unconfirmed", "The wake packet was not fully confirmed.", result.Outcome);
        }
        return reply.Value.Json.Deserialize<NodeResult>(ContractJson.Options) ?? throw new InvalidDataException("電源操作の応答がありません。");
    }

    /// <summary>停止し始めたPeerへ共有保存を要求せず、今回の結果だけを処理担当へ保持します。</summary>
    /// <returns>履歴を増やさない最新結果の保存です。</returns>
    internal async Task CompleteAsync(string hostId, HostPowerScheduleOccurrence occurrence, NodeResult result, CancellationToken cancellationToken = default)
    {
        string key = HostPowerScheduleStore.OccurrenceKey(hostId, occurrence.ScheduleId);
        DurableEntry? current = await repository.ReadAsync(HostPowerScheduleStore.Occurrences, key, cancellationToken).ConfigureAwait(false);
        HostPowerScheduleOccurrence? latest = current?.Value?.Json.Deserialize<HostPowerScheduleOccurrence>();
        if (latest?.OperationId != occurrence.OperationId)
        {
            return;
        }
        EOutcome saved = await repository.WriteLocalSnapshotAsync(HostPowerScheduleStore.ResultsScope(cluster.LocalPeerId), key,
            NodeValue.FromObject(occurrence with { Outcome = result.Outcome, Reason = result.ReasonCode }), cancellationToken).ConfigureAwait(false);
        if (saved != EOutcome.Succeeded)
        {
            throw new IOException("電源予定の実行結果を保存できません。");
        }
    }
}
