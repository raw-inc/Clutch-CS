using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Desktop;

/// <summary>選定したCoreの映像と入力をブラウザへ中継します。</summary>
internal static class DesktopRoutes
{
    /// <summary>映像を保存・再符号化しない専用経路を公開します。</summary>
    internal static void Map(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(2), KeepAliveTimeout = TimeSpan.FromSeconds(5) });
        app.MapGet("/client/desktop", (HttpContext context, ObserverConnection connection) => ConnectAsync(context, connection, context.RequestAborted));
    }

    /// <summary>明示clusterを尊重し、省略時は到達可能な候補を安定順で選びます。</summary>
    /// <returns>開始時に固定するclusterとpeerです。</returns>
    internal static (string Cluster, PeerInfo Peer) Select(IReadOnlyList<DiscoveredCluster> clusters, DesktopTarget target, Func<string, string, bool>? reachable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.HostId);
        foreach (DiscoveredCluster cluster in clusters.Where(item => string.IsNullOrEmpty(target.ClusterId) || string.Equals(item.ClusterId, target.ClusterId, StringComparison.Ordinal)).OrderBy(item => item.ClusterId, StringComparer.Ordinal))
        {
            foreach (PeerInfo peer in cluster.Peers.Where(item => string.Equals(item.HostId, target.HostId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(item.Status)).OrderBy(item => item.PeerId, StringComparer.Ordinal))
            {
                if (cluster.Peers.Count(item => string.Equals(item.PeerId, peer.PeerId, StringComparison.Ordinal)) == 1
                    && (reachable is null || reachable(cluster.ClusterId, peer.PeerId)))
                {
                    return (cluster.ClusterId, peer);
                }
            }
        }
        throw new InvalidOperationException("No unambiguous, ready peer exists for the selected host and cluster.");
    }

    /// <summary>中継Hostの所在地ではなく、閲覧端末と操作対象が同じPCかを確認します。</summary>
    /// <returns>自己入力・音声の循環を抑止する必要がある接続かです。</returns>
    internal static bool IsLocalViewer(HttpContext context, IPAddress peer, IEnumerable<IPAddress> hostAddresses)
    {
        if (ClientHttpPolicy.ViewerAddress(context) is not { } viewer)
        {
            return false;
        }
        IPAddress target = Normalize(peer);
        if (viewer.Equals(target))
        {
            return true;
        }
        HashSet<IPAddress> local = hostAddresses.Select(Normalize).ToHashSet();
        return (IPAddress.IsLoopback(viewer) || local.Contains(viewer)) && (IPAddress.IsLoopback(target) || local.Contains(target));
    }

    /// <summary>IPv4とIPv4-mapped IPv6の表記差を除きます。</summary>
    /// <returns>住所比較に使う値です。</returns>
    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        return address;
    }

    /// <summary>ブラウザの同一originと一回限りの接続許可を確認します。</summary>
    /// <returns>接続終了までの中継です。</returns>
    private static async Task ConnectAsync(HttpContext context, ObserverConnection connection, CancellationToken cancellationToken = default)
    {
        string expected = context.Request.Scheme + "://" + context.Request.Host;
        if (!context.WebSockets.IsWebSocketRequest || !string.Equals(context.Request.Headers.Origin.ToString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        try
        {
            await RelayAsync(context, connection, socket, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.ProtocolError, "Desktop.InvalidData", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException or SocketException)
        {
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.InternalServerError, "Desktop.UpstreamDisconnected", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>接続先の選定と開始失敗を、ブラウザーが区別できる終了理由へ変換します。</summary>
    /// <returns>開始拒否または中継の終了です。</returns>
    private static async Task RelayAsync(HttpContext context, ObserverConnection connection, WebSocket socket, CancellationToken cancellationToken = default)
    {
        DesktopTarget target = new(context.Request.Query["host"].ToString(), context.Request.Query["cluster"].ToString());
        (string cluster, PeerInfo peer) selected;
        try
        {
            selected = Select(connection.GetClusters(), target, (clusterId, peerId) => connection.FindObserver(clusterId, peerId) is not null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.InternalServerError, "Desktop.HostUnavailable", cancellationToken).ConfigureAwait(false);
            return;
        }
        (string cluster, PeerInfo peer) = selected;
        UdpObserver? observer = connection.FindObserver(cluster, peer.PeerId);
        if (observer is null)
        {
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.InternalServerError, "Desktop.HostUnavailable", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 旧ClientHostが厳密に読むticketは拡張せず、能力を独立した参照操作で確認します。
        ManagementReply capability = await observer.InspectAsync(new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId,
            DateTimeOffset.UtcNow.AddSeconds(15), "desktop.vbr", null), cancellationToken).ConfigureAwait(false);
        bool variableBitrate = capability.Outcome == EOutcome.Succeeded && capability.Value?.Json.ValueKind == JsonValueKind.True;
        ManagementReply inputCapability = await observer.InspectAsync(new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId,
            DateTimeOffset.UtcNow.AddSeconds(15), "desktop.viewOnly", null), cancellationToken).ConfigureAwait(false);
        bool viewOnly = inputCapability.Outcome == EOutcome.Succeeded && inputCapability.Value?.Json.ValueKind == JsonValueKind.True;
        ManagementReply annotationCapability = await observer.InspectAsync(new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId,
            DateTimeOffset.UtcNow.AddSeconds(15), "desktop.annotations", null), cancellationToken).ConfigureAwait(false);
        bool annotations = annotationCapability.Outcome == EOutcome.Succeeded && annotationCapability.Value?.Json.ValueKind == JsonValueKind.True;
        bool timing = false;
        bool clipboardText = false;
        string enabled = "1";
        string clipboardQuery = context.Request.Query["clipboard"].ToString();
        if (string.Equals(clipboardQuery, enabled, StringComparison.Ordinal))
        {
            ManagementReply clipboardCapability = await observer.InspectAsync(new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(15), "desktop.clipboard", null), cancellationToken).ConfigureAwait(false);
            clipboardText = clipboardCapability.Outcome == EOutcome.Succeeded && clipboardCapability.Value?.Json.ValueKind == JsonValueKind.True;
        }
        string timingQuery = context.Request.Query["timing"].ToString();
        if (string.Equals(timingQuery, enabled, StringComparison.Ordinal))
        {
            ManagementReply timingCapability = await observer.InspectAsync(new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(15), "desktop.timing", null), cancellationToken).ConfigureAwait(false);
            timing = timingCapability.Outcome == EOutcome.Succeeded && timingCapability.Value?.Json.ValueKind == JsonValueKind.True;
        }
        ManagementCommand command = new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(15), "desktop.open", NodeValue.FromObject(Guid.NewGuid()));
        ManagementReply reply = await observer.NotifyAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.InternalServerError, "Desktop.SessionUnavailable", cancellationToken).ConfigureAwait(false);
            return;
        }
        DesktopTicket ticket = reply.Value.Json.Deserialize<DesktopTicket>(ContractJson.Options) ?? throw new InvalidDataException("Desktop ticket is missing.");
        if (ticket.Port is < 1 or > 65535 || ticket.Token.Length != 64 || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("Desktop ticket is invalid.");
        }
        using TcpClient client = new(new IPEndPoint(IPAddress.Parse(observer.CaptureBindingStatus().Binding.BindAddress), 0));
        client.NoDelay = true;
        using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Parse(peer.Address), ticket.Port, connect.Token).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(ticket.Token), connect.Token).ConfigureAwait(false);
        bool localAudioMuted = IsLocalViewer(context, IPAddress.Parse(peer.Address), ObserverNetwork.GetInterfaces().SelectMany(item => item.Addresses).Select(address => IPAddress.Parse(address.Address)));
        byte[] description = JsonSerializer.SerializeToUtf8Bytes(new { clusterId = cluster, peer.HostId, peer.PeerId, localAudioMuted, cursorShapes = true, ticket.CodecDetails, viewOnly, variableBitrate, annotations, timing, clipboardText }, ContractJson.Options);
        byte[] header = new byte[DesktopWire.HeaderSize];
        DesktopWire.WriteHeader(header, 7, 0, description.Length, false, 0);
        await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Binary, false, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(description.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        try
        {
            await DesktopRelay.RunAsync(stream, socket, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DesktopConnectionClose.DrainUpstreamAsync(client, stream, CancellationToken.None).ConfigureAwait(false);
        }
        await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.NormalClosure, "Desktop.Closed", cancellationToken).ConfigureAwait(false);
    }
}
