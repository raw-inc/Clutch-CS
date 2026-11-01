using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実TCPとpipeで、時刻照会と元の映像・入力の境界を検証します。</summary>
public sealed class DesktopStreamTimingTests
{
    /// <summary>映像が出力を占有中でも、時計照会の処理は入力の受付を止めません。</summary>
    /// <returns>混雑時の計測省略と、次の照会での復帰確認です。</returns>
    [Fact]
    public async Task CongestedVideoDoesNotBlockInputWithAClockReplyAsync()
    {
        using MemoryStream output = new();
        using SemaphoreSlim writes = new(0, 1);
        DesktopStreamTiming timing = new(output, writes);
        byte[] request = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(request, 14);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4), 1);
        using CancellationTokenSource lifetime = new();
        Task replying = timing.ReplyAsync(request, lifetime.Token);
        try
        {
            await replying.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            Assert.Equal(0, output.Length);
            writes.Release();
            replying = timing.ReplyAsync(request, lifetime.Token);
            await replying.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            Assert.Equal(48, output.Length);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await replying.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>要求前は旧形式を維持し、要求後も映像の取得時刻とペイロードを変えません。</summary>
    /// <returns>時計応答、送信開始の印、入力への非混入の確認です。</returns>
    // 抑制理由: finallyでbridgeを回収してからstreamを破棄します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyで中継taskを回収します。")]
    public async Task AddsTimingOnlyAfterNegotiationAndKeepsOriginalFramesAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient remote = new();
        await remote.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).ConfigureAwait(true);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using NetworkStream network = accepted.GetStream();
        using NetworkStream viewer = remote.GetStream();
        string name = "desktop-timing-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream worker = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(pipe.WaitForConnectionAsync(timeout.Token), worker.ConnectAsync(timeout.Token)).ConfigureAwait(true);
        using Task bridge = DesktopSession.BridgeAsync(network, pipe, timeout.Token);
        try
        {
            byte[] frame = new byte[27];
            DesktopWire.WriteHeader(frame, 2, 0, 3, true, 1234);
            frame[24] = 1;
            frame[25] = 2;
            frame[26] = 3;
            await worker.WriteAsync(frame, timeout.Token).ConfigureAwait(true);
            byte[] original = new byte[frame.Length];
            await viewer.ReadExactlyAsync(original, timeout.Token).ConfigureAwait(true);
            Assert.Equal(frame, original);
            byte[] probe = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(probe, 14);
            BinaryPrimitives.WriteInt32BigEndian(probe.AsSpan(4), 42);
            BinaryPrimitives.WriteInt64BigEndian(probe.AsSpan(8), 5678000);
            await viewer.WriteAsync(probe, timeout.Token).ConfigureAwait(true);
            byte[] reply = new byte[48];
            await viewer.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(true);
            Assert.Equal(24, DesktopWire.PayloadLength(reply.AsSpan(0, 24)));
            Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(reply));
            Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(24)));
            Assert.Equal(5678000, BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(32)));
            long sent = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(16));
            Assert.InRange(BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(40)), 0, sent);
            await worker.WriteAsync(frame, timeout.Token).ConfigureAwait(true);
            byte[] marker = new byte[32];
            await viewer.ReadExactlyAsync(marker, timeout.Token).ConfigureAwait(true);
            Assert.Equal(14, BinaryPrimitives.ReadInt32BigEndian(marker));
            Assert.Equal(8, DesktopWire.PayloadLength(marker.AsSpan(0, 24)));
            Assert.Equal(1234, BinaryPrimitives.ReadInt64BigEndian(marker.AsSpan(24)));
            Assert.InRange(BinaryPrimitives.ReadInt64BigEndian(marker.AsSpan(16)), sent, long.MaxValue);
            await viewer.ReadExactlyAsync(original, timeout.Token).ConfigureAwait(true);
            Assert.Equal(frame, original);
            byte[] text = System.Text.Encoding.UTF8.GetBytes("Clipboard\r\n日本語🌐"), clipboard = new byte[24 + text.Length];
            BinaryPrimitives.WriteInt32BigEndian(clipboard, 16);
            BinaryPrimitives.WriteInt32BigEndian(clipboard.AsSpan(4), 7);
            BinaryPrimitives.WriteInt32BigEndian(clipboard.AsSpan(8), text.Length);
            text.CopyTo(clipboard, 24);
            await viewer.WriteAsync(clipboard.AsMemory(0, 13), timeout.Token).ConfigureAwait(true);
            await viewer.WriteAsync(clipboard.AsMemory(13), timeout.Token).ConfigureAwait(true);
            byte[] receivedText = new byte[clipboard.Length];
            await worker.ReadExactlyAsync(receivedText, timeout.Token).ConfigureAwait(true);
            Assert.Equal(clipboard, receivedText);
            BinaryPrimitives.WriteInt32BigEndian(probe, 4);
            await viewer.WriteAsync(probe, timeout.Token).ConfigureAwait(true);
            byte[] input = new byte[24];
            await worker.ReadExactlyAsync(input, timeout.Token).ConfigureAwait(true);
            Assert.Equal(probe, input);
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            try
            {
                await bridge.ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // 借用資源を閉じる前に両方向の終了を回収します。
            }
        }
    }

    /// <summary>時計応答と送信印の破損を転送前に拒否します。</summary>
    [Theory]
    [InlineData(13, 0, 23, false)]
    [InlineData(13, 1, 24, false)]
    [InlineData(13, 0, 24, true)]
    [InlineData(14, 0, 7, false)]
    [InlineData(14, 0, 8, true)]
    [InlineData(15, 0, 8, false)]
    public void RejectsMalformedTimingFrames(int kind, int monitor, int length, bool flag)
    {
        byte[] header = new byte[24];
        DesktopWire.WriteHeader(header, kind, monitor, length, flag, 0);
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
    }
}
