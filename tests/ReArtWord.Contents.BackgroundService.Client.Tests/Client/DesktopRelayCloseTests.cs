using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.ClientHost.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実TCPとWebSocketで、Core切断と閲覧者による正常切断を区別します。</summary>
public sealed class DesktopRelayCloseTests
{
    /// <summary>未読フレームが残っていても、TCPを即時破棄せずFINとEOFで終了します。</summary>
    /// <returns>上流が正常終了を観測し、残りのフレームを送信できることです。</returns>
    // 抑制理由: peerTaskはfinallyで取消しと完了を確認してからstreamを解放します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyでpeerTaskを回収します。")]
    public async Task UpstreamSeesFinAndFinishesSendingBeforeDisposalAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using TcpClient server = await listener.AcceptTcpClientAsync(timeout.Token);
        using NetworkStream viewer = client.GetStream(), peer = server.GetStream();
        using Task peerTask = SendUntilFinAsync(peer, timeout.Token);
        try
        {
            await DesktopConnectionClose.DrainUpstreamAsync(client, viewer, timeout.Token);
            await peerTask.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
            Assert.False(timeout.IsCancellationRequested);
        }
        finally
        {
            await timeout.CancelAsync();
            await peerTask.ConfigureAwait(true);
        }
    }

    /// <summary>受信を閉じるまで送信済みデータを残し、FIN受信後にも末尾データを送り切ります。</summary>
    /// <returns>FINを観測した実TCP応答です。</returns>
    private static async Task SendUntilFinAsync(NetworkStream stream, CancellationToken cancellationToken = default)
    {
        byte[] frame = new byte[262144];
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        stream.Close();
    }

    /// <summary>中継停止時の取消しで、元の切断原因を上書きしません。</summary>
    /// <returns>終了コードと理由を実WebSocketで受信する確認です。</returns>
    // 抑制理由: finallyで中継の終了を確認してから通信資源を解放します。
    [Theory, InlineData(false), InlineData(true), SuppressMessage("Reliability", "CA2025", Justification = "finallyで中継taskを回収します。")]
    public async Task SendsTheConfirmedClosureInsteadOfAbortingAsync(bool coreDisconnects)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient viewerClient = new();
        await viewerClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).ConfigureAwait(true);
        using TcpClient relayClient = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using WebSocket viewer = WebSocket.CreateFromStream(viewerClient.GetStream(), false, null, Timeout.InfiniteTimeSpan);
        using WebSocket relaySocket = WebSocket.CreateFromStream(relayClient.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        string name = "desktop-close-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream upstream = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream core = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using Task attached = upstream.WaitForConnectionAsync(timeout.Token);
        try
        {
            await core.ConnectAsync(timeout.Token).ConfigureAwait(true);
            await attached.ConfigureAwait(true);
        }
        finally
        {
            if (!attached.IsCompleted)
            {
                await timeout.CancelAsync().ConfigureAwait(true);
            }
            await attached.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        using Task relay = DesktopRelay.RunAsync(upstream, relaySocket, timeout.Token);
        try
        {
            WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure;
            string reason = "Desktop.Closed";
            if (coreDisconnects)
            {
                core.Close();
                await relay.ConfigureAwait(true);
                status = WebSocketCloseStatus.InternalServerError;
                reason = "Desktop.UpstreamDisconnected";
            }
            else
            {
                await viewer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "manual", timeout.Token).ConfigureAwait(true);
                await relay.ConfigureAwait(true);
            }
            await DesktopConnectionClose.SendAsync(relaySocket, status, reason, timeout.Token).ConfigureAwait(true);
            byte[] buffer = new byte[24];
            WebSocketReceiveResult closed = await viewer.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(true);
            Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
            Assert.Equal(status, closed.CloseStatus);
            Assert.Equal(reason, closed.CloseStatusDescription);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            try
            {
                await relay.ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or WebSocketException)
            {
                // 検証済みの切断例外も、資源の解放前に必ず回収します。
            }
        }
    }
}
