using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Terminal;

/// <summary>既存PTYの入出力だけを、固定Coreとブラウザーの間で中継します。</summary>
internal static class TerminalRoutes
{
    /// <summary>持続接続と、ブラウザー間で共有する一覧照会を公開します。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapGet("/client/terminal", ConnectAsync);
        app.MapGet("/client/terminal/sessions", (TerminalCatalog catalog, CancellationToken token) => catalog.ReadAsync(token));
    }

    /// <summary>同一origin・Peer実体・確定済み所有者を確認して中継を開始します。</summary>
    /// <returns>表示接続の終了です。</returns>
    private static async Task ConnectAsync(HttpContext context, ObserverConnection connection, PeerManagementClient remote, CancellationToken cancellationToken = default)
    {
        string origin = context.Request.Scheme + "://" + context.Request.Host;
        if (!context.WebSockets.IsWebSocketRequest || !string.Equals(context.Request.Headers.Origin.ToString(), origin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        IQueryCollection query = context.Request.Query;
        FileTransferPeer target = new(query["cluster"].ToString(), query["peer"].ToString(), Guid.Parse(query["instance"].ToString()));
        PeerInfo peer = connection.GetClusters().Where(item => string.Equals(item.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(item => item.Peers).Single(item => string.Equals(item.PeerId, target.PeerId, StringComparison.Ordinal) && item.InstanceId == target.InstanceId);
        UdpObserver observer = connection.FindObserver(target.ClusterId, target.PeerId) ?? throw new IOException("The peer route is unavailable.");
        TerminalAttachRequest request = new(Guid.Parse(query["session"].ToString()), Guid.Parse(query["owner"].ToString()), Guid.NewGuid());
        TerminalTicket ticket = await remote.SendAsync<TerminalTicket>(target, "terminal.attach", request, false, cancellationToken).ConfigureAwait(false);
        if (ticket.Port is < 1 or > 65535 || ticket.Token.Length != 64 || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("Invalid terminal ticket.");
        }

        using TcpClient client = new(new IPEndPoint(IPAddress.Parse(observer.CaptureBindingStatus().Binding.BindAddress), 0));
        client.NoDelay = true;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Parse(peer.Address), ticket.Port, timeout.Token).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(ticket.Token), timeout.Token).ConfigureAwait(false);
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(40),
        }).ConfigureAwait(false);
        try
        {
            await TerminalRelay.RunAsync(stream, socket, cancellationToken).ConfigureAwait(false);
        }

        catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException or SocketException)
        {
            socket.Abort();
        }
    }
}

