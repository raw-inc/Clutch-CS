using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;
using ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実TCP・WebSocket・pipeでバイナリ境界と切断時の回収を確認します。</summary>
public sealed class SignalScopeRelayTests
{
    /// <summary>通常配信は一覧だけとし、選択時だけ原本を転送します。制御の断片境界も維持します。</summary>
    /// <returns>双方向転送と終了の確認です。</returns>
    // 抑制理由: finallyでrelayを取消し、全通信の終了後に所有資源を解放します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyでrelayの完了を待ちます。")]
    public async Task FragmentedCommandsAndBinaryPacketsKeepTheirBoundariesAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient viewerClient = new();
        await viewerClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).ConfigureAwait(true);
        using TcpClient relayClient = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using WebSocket viewer = WebSocket.CreateFromStream(viewerClient.GetStream(), false, null, Timeout.InfiniteTimeSpan);
        using WebSocket relaySocket = WebSocket.CreateFromStream(relayClient.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        string name = "signal-scope-test-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream upstream = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream capture = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using Task attaching = upstream.WaitForConnectionAsync(timeout.Token);
        try
        {
            await capture.ConnectAsync(timeout.Token).ConfigureAwait(true);
            await attaching.ConfigureAwait(true);
        }
        finally
        {
            if (!attaching.IsCompleted)
            {
                await timeout.CancelAsync().ConfigureAwait(true);
            }
            await attaching.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        using Task relay = SignalScopeRelay.RunAsync(upstream, relaySocket, cancellationToken: timeout.Token);
        try
        {
            byte[] command = "{\"type\":\"list\"}"u8.ToArray();
            await viewer.SendAsync(command.AsMemory(0, 4), WebSocketMessageType.Text, false, timeout.Token).ConfigureAwait(true);
            await viewer.SendAsync(command.AsMemory(4), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(true);
            byte[] buffer = new byte[16389];
            int length = await SignalScopeWire.ReadAsync(capture, buffer, timeout.Token).ConfigureAwait(true);
            Assert.Equal(command.Length + 1, length);
            Assert.Equal(1, buffer[4]);
            Assert.Equal(command, buffer.AsSpan(5, length - 1).ToArray());

            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new { sourceId = "pcap:test", observationPoint = "rx", time = "2026-10-07T00:00:00Z", linkType = 1,
                originalLength = 9000, source = "192.0.2.1", destination = "192.0.2.2", sourcePort = 1234, destinationPort = 5678, protocol = "OSC", transport = "UDP",
                summary = "試験OSC", fingerprint = new string('a', 64), details = "The detailed payload is available only on selection." });
            byte[] packet = new byte[9 + metadata.Length + 9000];
            BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length - 4);
            packet[4] = 2;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5), metadata.Length);
            metadata.CopyTo(packet, 9);
            for (int index = 9 + metadata.Length; index < packet.Length; index++)
            {
                packet[index] = (byte)(index % 256);
            }
            await capture.WriteAsync(packet.AsMemory(0, 3), timeout.Token).ConfigureAwait(true);
            await capture.WriteAsync(packet.AsMemory(3), timeout.Token).ConfigureAwait(true);
            byte[] received = new byte[packet.Length - 4];
            int offset = 0;
            ValueWebSocketReceiveResult piece;
            do
            {
                piece = await viewer.ReceiveAsync(received.AsMemory(offset, Math.Min(113, received.Length - offset)), timeout.Token).ConfigureAwait(true);
                Assert.Equal(WebSocketMessageType.Binary, piece.MessageType);
                offset += piece.Count;
            }
            while (!piece.EndOfMessage);
            Assert.Equal(2, received[0]);
            int summaryLength = BinaryPrimitives.ReadInt32BigEndian(received.AsSpan(1));
            Assert.Equal(summaryLength + 5, offset);
            Assert.True(offset < packet.Length / 4);
            using JsonDocument summary = JsonDocument.Parse(received.AsMemory(5, summaryLength));
            Assert.False(summary.RootElement.TryGetProperty("details", out _));
            Assert.Equal("試験OSC", summary.RootElement.GetProperty("summary").GetString());
            await viewer.SendAsync("{\"type\":\"detail\",\"packetId\":1,\"requestId\":9}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(true);
            byte[] detail = new byte[16384];
            offset = 0;
            do
            {
                piece = await viewer.ReceiveAsync(detail.AsMemory(offset), timeout.Token).ConfigureAwait(true);
                offset += piece.Count;
            }
            while (!piece.EndOfMessage);
            Assert.Equal(3, detail[0]);
            int detailLength = BinaryPrimitives.ReadInt32BigEndian(detail.AsSpan(1));
            Assert.Equal(packet.AsSpan(9 + metadata.Length).ToArray(), detail.AsSpan(5 + detailLength, offset - 5 - detailLength).ToArray());
            await viewer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            try
            {
                await relay.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 正常切断でも反対向きの保留readは取消されます。
            }
        }
    }

    /// <summary>長さだけの不正フレームで巨大領域を確保しません。</summary>
    /// <returns>境界検証です。</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2097153)]
    public async Task InvalidFrameLengthIsRejectedBeforeReadingTheBodyAsync(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        using MemoryStream stream = new(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => SignalScopeWire.ReadAsync(stream, new byte[32])).ConfigureAwait(true);
    }

    /// <summary>JSON制御にkindと長さを付けます。</summary>
    [Fact]
    public void ControlFramesHaveAnExplicitKindAndLength()
    {
        byte[] value = SignalScopeWire.Control(JsonSerializer.SerializeToElement(new { type = "stop" }));
        Assert.Equal(value.Length - 4, BinaryPrimitives.ReadInt32BigEndian(value));
        Assert.Equal(1, value[4]);
        using JsonDocument json = JsonDocument.Parse(value.AsMemory(5));
        Assert.Equal("stop", json.RootElement.GetProperty("type").GetString());
    }
}
