using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>対象PCが停止中でも、明示したクラスタの稼働Coreへ電源予定の管理を接続します。</summary>
internal sealed class HostPowerScheduleRemote(ObserverConnection connection, ClientCommandLedger ledger, WakeHostCache wake)
{
    private static string SaveOperation => "host.power-schedules.save";

    /// <summary>稼働中または停止前に確認した所属だけを候補にします。</summary>
    /// <returns>このClientHostから到達候補があるクラスタです。</returns>
    internal async Task<IReadOnlyList<string>> ClustersAsync(string hostId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        IReadOnlyList<Contracts.Management.Overview.HostWakeProfile> profiles = await ProfilesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> known = profiles.FirstOrDefault(profile => string.Equals(profile.HostId, hostId, StringComparison.Ordinal))?.Clusters ?? [];
        return connection.GetClusters().Where(cluster => known.Contains(cluster.ClusterId, StringComparer.Ordinal)
                || cluster.Peers.Any(peer => string.Equals(peer.HostId, hostId, StringComparison.Ordinal)))
            .Select(cluster => cluster.ClusterId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>停止したHostを、OS設定の操作能力を付けずに予定編集の候補へ残します。</summary>
    /// <returns>実発見情報を優先したHost一覧です。</returns>
    internal async Task<IReadOnlyList<DeviceHostInfo>> IncludeOfflineAsync(IReadOnlyList<DeviceHostInfo> online, CancellationToken cancellationToken = default)
    {
        List<DeviceHostInfo> result = [.. online];
        HashSet<string> availableClusters = connection.GetClusters().Select(cluster => cluster.ClusterId).ToHashSet(StringComparer.Ordinal);
        foreach (Contracts.Management.Overview.HostWakeProfile profile in await ProfilesAsync(cancellationToken).ConfigureAwait(false))
        {
            string? cluster = profile.Clusters.FirstOrDefault(availableClusters.Contains);
            if (cluster is not null && !result.Any(host => string.Equals(host.Target.HostId, profile.HostId, StringComparison.Ordinal)))
            {
                result.Add(new(new(profile.HostId, cluster, string.Empty, Guid.Empty), null, "Device.HostUnavailable"));
            }
        }
        return result.OrderBy(host => host.Target.HostId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>別クラスタへ暗黙に切り替えず、選択したクラスタの予定を参照します。</summary>
    /// <returns>担当Coreが観測した予定と直近状態です。</returns>
    internal async Task<HostPowerScheduleView> ReadAsync(HostPowerScheduleReadRequest request, CancellationToken cancellationToken = default)
    {
        (UdpObserver observer, PeerInfo peer) = Resolve(request.ClusterId);
        ManagementReply reply = await observer.InspectAsync(new(Guid.NewGuid(), request.ClusterId, peer.PeerId, peer.InstanceId,
            DateTimeOffset.UtcNow.AddSeconds(15), "host.power-schedules.read", ContractJson.ToValue(request)), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new InvalidOperationException("powerSchedule.readUnavailable");
        }
        return reply.Value.Json.Deserialize<HostPowerScheduleView>(ContractJson.Options) ?? throw new InvalidDataException("電源予定の応答がありません。");
    }

    /// <summary>保存対象と内容を先に固定し、結果不明の要求を他Peerから再実行しません。</summary>
    /// <returns>元の保存の結果です。</returns>
    internal async Task<HostPowerScheduleSaveResult> SaveAsync(HostPowerScheduleSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty)
        {
            return new(EOutcome.NotExecuted, "powerSchedule.invalid");
        }
        Guid id = ClientCommandLedger.Identity(request.OperationId, SaveOperation);
        bool sent = false;
        try
        {
            if (await ledger.ReadAsync(id, cancellationToken).ConfigureAwait(false) is { } original)
            {
                sent = true;
                if (!string.Equals(original.ClusterId, request.ClusterId, StringComparison.Ordinal) || !string.Equals(original.Operation, SaveOperation, StringComparison.Ordinal))
                {
                    return new(EOutcome.NotExecuted, "powerSchedule.invalid");
                }
                UdpObserver observer = connection.FindObserver(original.ClusterId, original.TargetPeerId) ?? throw new IOException("powerSchedule.clusterUnavailable");
                ManagementQueryResult query = await observer.QueryAsync(new ManagementReceiptQuery(original.RequestId, original.ClusterId, original.TargetPeerId, original.RequestFingerprint), cancellationToken).ConfigureAwait(false);
                HostPowerScheduleSaveResult result = Result(query.Reply);
                if (result.View is { } view && (!string.Equals(view.HostId, request.HostId, StringComparison.Ordinal)
                    || !string.Equals(ContractJson.Hash(view.Schedules), ContractJson.Hash(request.Schedules), StringComparison.Ordinal)))
                {
                    return new(EOutcome.NotExecuted, "powerSchedule.invalid");
                }
                return result;
            }
            request = request with { WakeProfile = (await ProfilesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(profile =>
                string.Equals(profile.HostId, request.HostId, StringComparison.Ordinal) && profile.Clusters.Contains(request.ClusterId, StringComparer.Ordinal)) };
            NodeValue payload = ContractJson.ToValue(request);
            (UdpObserver selected, PeerInfo peer) = Resolve(request.ClusterId);
            ManagementCommand command = await ledger.PrepareAsync(id, request.ClusterId, peer.PeerId, SaveOperation, payload, peer, cancellationToken).ConfigureAwait(false);
            sent = true;
            return Result(await selected.ExecuteAsync(command, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        {
            return new(sent ? EOutcome.Unknown
                            : EOutcome.NotExecuted, "powerSchedule.saveUnconfirmed");
        }
    }

    /// <summary>唯一のCoordinatorを優先し、候補が割れているときは保存先を推測しません。</summary>
    /// <returns>選択クラスタの固定実体です。</returns>
    private (UdpObserver Observer, PeerInfo Peer) Resolve(string clusterId)
    {
        DiscoveredCluster cluster = connection.GetClusters().SingleOrDefault(item => string.Equals(item.ClusterId, clusterId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("powerSchedule.clusterUnavailable");
        string[] coordinators = cluster.CoordinatorCandidates.Distinct(StringComparer.Ordinal).ToArray();
        if (coordinators.Length != 1)
        {
            throw new InvalidOperationException("powerSchedule.clusterUnavailable");
        }
        PeerInfo? peer = cluster.Peers.FirstOrDefault(item => string.Equals(item.PeerId, coordinators[0], StringComparison.Ordinal)
            && PeerStatusRules.CanParticipate(item.Status) && DateTimeOffset.UtcNow - item.LastSeen <= TimeSpan.FromSeconds(15));
        if (peer is null || connection.FindObserver(clusterId, peer.PeerId) is not { } observer)
        {
            throw new InvalidOperationException("powerSchedule.clusterUnavailable");
        }
        return (observer, peer);
    }

    /// <summary>外側の受理と設定保存の成功を区別します。</summary>
    /// <returns>未確認を成功へ補完しないドメイン結果です。</returns>
    private static HostPowerScheduleSaveResult Result(ManagementReply? reply)
    {
        if (reply?.Outcome == EOutcome.Succeeded && reply.Value is not null)
        {
            return reply.Value.Json.Deserialize<HostPowerScheduleSaveResult>(ContractJson.Options) ?? new(EOutcome.Unknown, "powerSchedule.saveUnconfirmed");
        }
        EOutcome outcome = reply?.Outcome ?? EOutcome.Unknown;
        if (outcome == EOutcome.Succeeded)
        {
            outcome = EOutcome.Unknown;
        }
        return new(outcome, reply?.Reason ?? "powerSchedule.saveUnconfirmed");
    }

    /// <summary>停止前情報の破損で、現在発見できるHostのOS管理まで利用不能にしません。</summary>
    /// <returns>確認できたキャッシュだけです。稼働中Hostは発見情報から別途追加します。</returns>
    private async Task<IReadOnlyList<Contracts.Management.Overview.HostWakeProfile>> ProfilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await wake.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }
}
