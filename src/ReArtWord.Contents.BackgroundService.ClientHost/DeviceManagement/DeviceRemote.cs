using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>Host/Peer/実体を照合して既存の管理輸送へ接続します。</summary>
internal sealed class DeviceRemote(ObserverConnection connection, ClientCommandLedger ledger) : IDeviceRemote
{
    /// <summary>同一PCの複数Peerから応答する一件を選び、未対応も表示用に保持します。</summary>
    /// <returns>Hostごとの能力一覧です。</returns>
    internal async Task<IReadOnlyList<DeviceHostInfo>> HostsAsync(CancellationToken cancellationToken = default)
    {
        DeviceHostTarget[] targets = connection.GetClusters().SelectMany(cluster => cluster.Peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.HostId) && DateTimeOffset.UtcNow - peer.LastSeen < TimeSpan.FromSeconds(15))
            .Select(peer => new DeviceHostTarget(peer.HostId!, cluster.ClusterId, peer.PeerId, peer.InstanceId))).ToArray();
        List<DeviceHostInfo> hosts = [];
        foreach (IGrouping<string, DeviceHostTarget> group in targets.GroupBy(target => target.HostId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            DeviceHostInfo host = new(group.First(), null, "Device.HostUnavailable");
            foreach (DeviceHostTarget target in group.OrderBy(target => target.ClusterId, StringComparer.Ordinal).ThenBy(target => target.PeerId, StringComparer.Ordinal))
            {
                try
                {
                    DeviceCapabilities? capabilities = await InspectAsync<DeviceCapabilities>(target, "host-manager.capabilities", null, false, cancellationToken).ConfigureAwait(false);
                    if (capabilities is not null)
                    {
                        host = new(target, capabilities);
                        break;
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            hosts.Add(host);
        }
        return hosts;
    }

    /// <summary>変更対象の設定と版を参照します。</summary>
    /// <returns>同じ実体の設定です。</returns>
    public async Task<DeviceSnapshot> ReadAsync(DeviceTargetRead request, CancellationToken cancellationToken = default)
    {
        DeviceChangeRules.ValidateRead(request.Query);
        return await InspectAsync<DeviceSnapshot>(request.Target, "host-manager.read", NodeValue.FromObject(request.Query), false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Device.ReadUnavailable");
    }

    /// <summary>元操作の受付記録を作り、一度だけ送信します。</summary>
    /// <returns>確認した変更結果です。</returns>
    public async Task<DeviceChangeResult> ApplyAsync(DevicePlannedChange change, CancellationToken cancellationToken = default)
    {
        DeviceHostTarget target = change.Target;
        bool sent = false;
        try
        {
            (UdpObserver observer, PeerInfo peer) = Resolve(target, false);
            string operation = "host-manager.apply";
            ManagementCommand command = await ledger.PrepareAsync(ClientCommandLedger.Identity(change.Request.OperationId, operation),
                target.ClusterId, target.PeerId, operation, NodeValue.FromObject(change.Request), peer, cancellationToken).ConfigureAwait(false);
            sent = true;
            ManagementReply reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            if (reply.Outcome == EOutcome.Succeeded && reply.Value is not null)
            {
                DeviceChangeResult result = DeviceSnapshots.Read<DeviceChangeResult>(reply.Value.Json);
                if (result.OperationId == change.Request.OperationId)
                {
                    return result;
                }
                return new(change.Request.OperationId, EOutcome.Unknown, "Device.ResultUnconfirmed");
            }
            DeviceChangeResult outcome = new(change.Request.OperationId, reply.Outcome, reply.Reason);
            if (reply.Outcome == EOutcome.Unknown)
            {
                return await ObserveCompletionAsync(change, outcome, cancellationToken).ConfigureAwait(false);
            }
            return outcome;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        {
            EOutcome outcome = sent ? EOutcome.Unknown
                                    : EOutcome.NotExecuted;
            return new(change.Request.OperationId, outcome, "Device.TargetUnavailable");
        }
    }

    /// <summary>再起動後も同じPCの元結果だけを参照します。</summary>
    /// <returns>保存済み結果または確認不能です。</returns>
    public async Task<DeviceChangeResult?> ResultAsync(DevicePlannedChange change, CancellationToken cancellationToken = default)
    {
        try
        {
            DeviceChangeResult? result = await InspectAsync<DeviceChangeResult>(change.Target, "host-manager.operations",
                NodeValue.FromObject(change.Request.OperationId.ToString("D")), true, cancellationToken).ConfigureAwait(false);
            if (result?.OperationId == change.Request.OperationId)
            {
                return result;
            }
            ClientCommandRecord? command = await ledger.ReadAsync(ClientCommandLedger.Identity(change.Request.OperationId, "host-manager.apply"), cancellationToken).ConfigureAwait(false);
            if (command is null)
            {
                return null;
            }
            (UdpObserver observer, _) = Resolve(change.Target, true);
            ManagementReceiptQuery query = new(command.RequestId, command.ClusterId, command.TargetPeerId, command.RequestFingerprint);
            ManagementQueryResult receipt = await observer.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            if (receipt.Reply?.Outcome == EOutcome.NotExecuted)
            {
                return new(change.Request.OperationId, EOutcome.NotExecuted, receipt.Reply.Reason);
            }
            if (receipt.Reply?.Outcome == EOutcome.Succeeded && receipt.Reply.Value is not null)
            {
                DeviceChangeResult confirmed = DeviceSnapshots.Read<DeviceChangeResult>(receipt.Reply.Value.Json);
                if (confirmed.OperationId == change.Request.OperationId)
                {
                    return confirmed;
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>OS処理が管理通信の待機時間を超えた場合、再送せず元結果だけを最長60秒照会します。</summary>
    /// <returns>完了を確認した結果、または元の不明状態です。</returns>
    private async Task<DeviceChangeResult> ObserveCompletionAsync(DevicePlannedChange change, DeviceChangeResult uncertain, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string pendingReason = "Device.Started";
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                DeviceChangeResult? result = await ResultAsync(change, timeout.Token).ConfigureAwait(false);
                if (result is not null && (result.Outcome != EOutcome.Unknown || !string.Equals(result.Reason, pendingReason, StringComparison.Ordinal)))
                {
                    return result;
                }
                await Task.Delay(1000, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 照会期限を過ぎても実行有無を推測しません。
        }
        return uncertain;
    }

    /// <summary>再起動した同じPeerへのアクセスは結果照会だけに限定します。</summary>
    /// <returns>照合した接続と実体です。</returns>
    private (UdpObserver Observer, PeerInfo Peer) Resolve(DeviceHostTarget target, bool recovery)
    {
        PeerInfo[] peers = connection.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, target.PeerId, StringComparison.Ordinal)).ToArray();
        if (peers.Length != 1 || !string.Equals(peers[0].HostId, target.HostId, StringComparison.Ordinal)
            || (!recovery && peers[0].InstanceId != target.InstanceId) || TimeSpan.FromSeconds(15) < DateTimeOffset.UtcNow - peers[0].LastSeen)
        {
            throw new InvalidOperationException("Device.TargetChanged");
        }
        return (connection.FindObserver(target.ClusterId, target.PeerId) ?? throw new InvalidOperationException("Device.TargetUnavailable"), peers[0]);
    }

    /// <summary>応答を得られない場合に別Peerへ対象を替えません。</summary>
    /// <returns>同じ実体の参照結果です。</returns>
    private async Task<T?> InspectAsync<T>(DeviceHostTarget target, string operation, NodeValue? payload, bool recovery, CancellationToken cancellationToken = default)
    {
        (UdpObserver observer, PeerInfo peer) = Resolve(target, recovery);
        ManagementReply reply = await observer.InspectAsync(new(Guid.NewGuid(), target.ClusterId, target.PeerId, peer.InstanceId,
            DateTimeOffset.UtcNow.AddMinutes(1), operation, payload), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidOperationException("Device.ReadUnavailable");
        }
        return reply.Value is null || reply.Value.Json.ValueKind == JsonValueKind.Null ? default
                                                                                       : DeviceSnapshots.Read<T>(reply.Value.Json);
    }
}
