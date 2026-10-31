using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ReArtWord.Contents.BackgroundService.ClientHost.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実WebSocketの分割入力を復元し、テキストと次のキーを同じ順序で中継します。</summary>
public sealed class DesktopClipboardRelayTests
{
    /// <summary>固定長入力との混在でも、本文へ生存通知を混入させません。</summary>
    /// <returns>大きいUnicode本文、次のキー、反映確認の実転送です。</returns>
    // 抑制理由: finallyで中継taskを回収してから通信資源を解放します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyでrelayをawaitします。")]
    public async Task FragmentedTextPrecedesTheNextKeyAndReceivesItsAcknowledgementAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient viewerClient = new();
        await viewerClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).ConfigureAwait(true);
        using TcpClient relayClient = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using WebSocket viewer = WebSocket.CreateFromStream(viewerClient.GetStream(), false, null, Timeout.InfiniteTimeSpan);
        using WebSocket relaySocket = WebSocket.CreateFromStream(relayClient.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        string name = "desktop-clipboard-" + Guid.NewGuid().ToString("N");
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
            byte[] text = Encoding.UTF8.GetBytes(new string('界', 20000) + "\r\n🌐"), packet = new byte[24 + text.Length];
            BinaryPrimitives.WriteInt32BigEndian(packet, 16);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), 42);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), text.Length);
            text.CopyTo(packet, 24);
            await viewer.SendAsync(packet.AsMemory(0, 11), WebSocketMessageType.Binary, false, timeout.Token).ConfigureAwait(true);
            await viewer.SendAsync(packet.AsMemory(11, 37), WebSocketMessageType.Binary, false, timeout.Token).ConfigureAwait(true);
            await viewer.SendAsync(packet.AsMemory(48), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
            byte[] key = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(key, 4);
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(16), 86);
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(20), 1);
            await viewer.SendAsync(key, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(true);
            byte[] header = await ReadInputAsync(core, timeout.Token).ConfigureAwait(true);
            Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(header));
            Assert.Equal(packet.Length, DesktopClipboardWire.InputLength(header));
            byte[] received = new byte[text.Length];
            await core.ReadExactlyAsync(received, timeout.Token).ConfigureAwait(true);
            Assert.Equal(text, received);
            Assert.Equal(key, await ReadInputAsync(core, timeout.Token).ConfigureAwait(true));
            byte[] expected = DesktopClipboardWire.Frame(1, 42, 9), response = new byte[expected.Length];
            await core.WriteAsync(expected, timeout.Token).ConfigureAwait(true);
            int offset = 0;
            ValueWebSocketReceiveResult piece;
            do
            {
                piece = await viewer.ReceiveAsync(response.AsMemory(offset), timeout.Token).ConfigureAwait(true);
                offset += piece.Count;
            }
            while (!piece.EndOfMessage);
            Assert.Equal(expected.Length, offset);
            Assert.Equal(expected, response);
            await viewer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).ConfigureAwait(true);
            await relay.ConfigureAwait(true);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            await relay.ConfigureAwait(true);
        }
    }

    /// <summary>本文の外にある生存通知だけを除いて、一入力のヘッダーを取得します。</summary>
    /// <returns>次の実入力です。</returns>
    private static async Task<byte[]> ReadInputAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[24];
        do
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        while (BinaryPrimitives.ReadInt32BigEndian(header) == 0);
        return header;
    }
}
