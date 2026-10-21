using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.ManagementTransport.State;

namespace ReArtWord.Contents.BackgroundService.ClientHost.States;

/// <summary>同じoriginの表示接続を、実体を固定したCoreの状態購読へ接続します。</summary>
internal static class StateStreamRoutes
{
    /// <summary>状態表示の一時的な中継を公開します。</summary>
    internal static void Map(WebApplication app) => app.MapGet("/client/state-stream", ConnectAsync);

    /// <summary>接続元とPeer実体を確認し、一回限りの許可値をブラウザーへ渡さずに中継します。</summary>
    /// <returns>ブラウザーとの接続終了です。</returns>
    private static async Task ConnectAsync(HttpContext context, ObserverConnection connection, PeerManagementClient remote, CancellationToken cancellationToken = default)
    {
        string origin = context.Request.Scheme + "://" + context.Request.Host;
        if (!context.WebSockets.IsWebSocketRequest || !string.Equals(context.Request.Headers.Origin.ToString(), origin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        string instanceName = "instance";
        if (!Guid.TryParse(context.Request.Query[instanceName], out Guid instance))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        FileTransferPeer target = new(context.Request.Query["cluster"].ToString(), context.Request.Query["peer"].ToString(), instance);
        PeerInfo? peer = connection.GetClusters().Where(item => string.Equals(item.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(item => item.Peers).SingleOrDefault(item => string.Equals(item.PeerId, target.PeerId, StringComparison.Ordinal) && item.InstanceId == instance);
        UdpObserver? observer = connection.FindObserver(target.ClusterId, target.PeerId);
        if (peer is null || observer is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(40),
        }).ConfigureAwait(false);
        try
        {
            StateStreamTicket ticket = await remote.SendAsync<StateStreamTicket>(target, "state-stream.open", Guid.NewGuid(), false, cancellationToken).ConfigureAwait(false);
            if (ticket.Port is < 1 or > 65535 || ticket.Token.Length != 64 || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidDataException("Invalid state observation ticket.");
            }
            using TcpClient client = new(new IPEndPoint(IPAddress.Parse(observer.CaptureBindingStatus().Binding.BindAddress), 0));
            client.NoDelay = true;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(IPAddress.Parse(peer.Address), ticket.Port, timeout.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Convert.FromHexString(ticket.Token), timeout.Token).ConfigureAwait(false);
            await StateStreamRelay.RunAsync(stream, socket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or WebSocketException or SocketException or InvalidOperationException or OperationCanceledException or JsonException or FormatException or NotSupportedException)
        {
            if (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using CancellationTokenSource closing = new(TimeSpan.FromSeconds(2));
                byte[] failure = JsonSerializer.SerializeToUtf8Bytes(new { problem = "State observation disconnected" });
                try
                {
                    await socket.SendAsync(failure.AsMemory(), WebSocketMessageType.Text, true, closing.Token).ConfigureAwait(false);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "State observation unavailable", closing.Token).ConfigureAwait(false);
                }
                catch (Exception closingError) when (closingError is WebSocketException or OperationCanceledException)
                {
                    socket.Abort();
                }
            }
        }
    }
}
