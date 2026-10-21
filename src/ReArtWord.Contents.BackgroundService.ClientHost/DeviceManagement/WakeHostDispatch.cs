using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>停止したPCの保存済み選択を、到達可能な一つのCoreから一度だけ送ります。</summary>
internal sealed class WakeHostDispatch(ObserverConnection connection, WakeHostCache cache, ClientCommandLedger ledger)
{
    private static string Operation => "host.wake";

    /// <summary>同じ操作IDの再要求は元結果の照会だけにし、別の送信元へ付け替えません。</summary>
    /// <returns>送信受付、未送信、または結果確認不能です。</returns>
    internal async Task<HostWakeResult> SendAsync(HostWakeStart request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
        if (request.OperationId == Guid.Empty || 120 < request.HostId.Length)
        {
            throw new ArgumentException("wake.invalidRequest", nameof(request));
        }
        Guid commandId = ClientCommandLedger.Identity(request.OperationId, Operation);
        bool submitted = false;
        try
        {
            ClientCommandRecord? prior = await ledger.ReadAsync(commandId, cancellationToken).ConfigureAwait(false);
            if (prior is not null)
            {
                submitted = true;
                return await ReadResultAsync(request.OperationId, prior, cancellationToken).ConfigureAwait(false);
            }
            (string Cluster, PeerInfo Peer)[] live = connection.GetClusters().SelectMany(cluster => cluster.Peers.Where(peer => DateTimeOffset.UtcNow - peer.LastSeen < TimeSpan.FromSeconds(15)
                && peer.Status is EPeerStatus.Ready or EPeerStatus.Syncing).Select(peer => (Cluster: cluster.ClusterId, Peer: peer))).ToArray();
            if (live.Any(item => string.Equals(item.Peer.HostId, request.HostId, StringComparison.Ordinal)))
            {
                return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.alreadyOnline");
            }
            IReadOnlyList<HostWakeProfile> saved = await cache.ReadAsync(cancellationToken).ConfigureAwait(false);
            HostWakeProfile? profile = saved.FirstOrDefault(value => string.Equals(value.HostId, request.HostId, StringComparison.Ordinal));
            if (profile is null)
            {
                return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.notLearned");
            }
            string[] macs = profile.State.Selected().Select(item => item.MacAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0)
            {
                return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.noInterfaces");
            }
            foreach (string mac in macs)
            {
                _ = WakeOnLanSender.Packet(mac);
            }
            (string Cluster, PeerInfo Peer) selected = live.OrderByDescending(item => NetworkScore(profile, item.Peer, saved))
                .ThenByDescending(item => profile.Clusters.Contains(item.Cluster, StringComparer.Ordinal))
                .ThenBy(item => item.Cluster, StringComparer.Ordinal).ThenBy(item => item.Peer.PeerId, StringComparer.Ordinal).FirstOrDefault();
            if (selected.Peer is null || connection.FindObserver(selected.Cluster, selected.Peer.PeerId) is not { } observer)
            {
                return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.noRelay");
            }
            HostWakeRequest payload = new(request.OperationId, request.HostId, macs);
            ManagementCommand command = await ledger.PrepareAsync(commandId, selected.Cluster, selected.Peer.PeerId, Operation,
                NodeValue.FromObject(payload), selected.Peer, cancellationToken).ConfigureAwait(false);
            submitted = true;
            ManagementReply reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            return Result(request.OperationId, reply);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            if (submitted)
            {
                return new(request.OperationId, EOutcome.Unknown, 0, 0, "wake.unconfirmed");
            }
            return new(request.OperationId, EOutcome.NotExecuted, 0, 0, "wake.unavailable");
        }
    }

    /// <summary>記録済み要求の結果を照会し、返事がない場合は送信し直しません。</summary>
    /// <returns>元の結果だけです。</returns>
    private async Task<HostWakeResult> ReadResultAsync(Guid id, ClientCommandRecord prior, CancellationToken cancellationToken = default)
    {
        UdpObserver? observer = connection.FindObserver(prior.ClusterId, prior.TargetPeerId);
        if (observer is null)
        {
            return new(id, EOutcome.Unknown, 0, 0, "wake.unconfirmed");
        }
        ManagementQueryResult result = await observer.QueryAsync(new ManagementReceiptQuery(prior.RequestId, prior.ClusterId, prior.TargetPeerId, prior.RequestFingerprint), cancellationToken).ConfigureAwait(false);
        if (result.Reply is { } reply)
        {
            return Result(id, reply);
        }
        return new(id, EOutcome.Unknown, 0, 0, "wake.unconfirmed");
    }

    /// <summary>取得した型付きの元結果以外を送信成功と解釈しません。</summary>
    /// <returns>送信先で確認された結果です。</returns>
    private static HostWakeResult Result(Guid id, ManagementReply reply)
    {
        if (reply.Outcome == EOutcome.Succeeded && reply.Value is { } value)
        {
            HostWakeResult? result = value.Json.Deserialize<HostWakeResult>(Contracts.Serialization.ContractJson.Options);
            if (result?.OperationId == id)
            {
                return result;
            }
        }
        if (reply.Outcome == EOutcome.NotExecuted)
        {
            return new(id, EOutcome.NotExecuted, 0, 0, "wake.relayUnavailable");
        }
        return new(id, EOutcome.Unknown, 0, 0, "wake.unconfirmed");
    }

    /// <summary>対象PCとIPv4リンクを共有していると確認できた送信元を優先します。</summary>
    /// <returns>NIC情報が既知の候補と、そのネットワーク一致度です。</returns>
    internal static int NetworkScore(HostWakeProfile target, PeerInfo peer, IReadOnlyList<HostWakeProfile> profiles)
    {
        HostWakeProfile? source = profiles.FirstOrDefault(profile => string.Equals(profile.HostId, peer.HostId, StringComparison.Ordinal));
        IEnumerable<string> addresses = (source?.State.Inventory.Addresses.Select(item => item.Address) ?? []).Append(peer.Address);
        int score = source is null ? 0
                                   : 1;
        return score + addresses.Select(address => target.State.Inventory.Addresses.Count(network => InSubnet(address, network))).DefaultIfEmpty(0).Max() * 10;
    }

    /// <summary>同じネットワークかだけを判定し、DNSやARPの照会を行いません。</summary>
    /// <returns>保存されたIPv4プレフィックス内の候補かです。</returns>
    internal static bool InSubnet(string address, DeviceIpAddress network)
        => WakeOnLanRoute.Contains(address, network);
}
