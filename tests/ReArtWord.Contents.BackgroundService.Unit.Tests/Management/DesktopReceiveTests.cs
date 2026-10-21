using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実UDPでSR遅着・時刻ずれを与え、接続を保って映像が復帰することを確認します。</summary>
public sealed class DesktopReceiveTests
{
    /// <summary>未同期の先頭映像と古い映像を捨て、二つの同期済みIDRだけを中継します。</summary>
    /// <returns>取得機器や本番への接続を使わない受信経路の確認です。</returns>
    // 抑制理由: 受信taskをfinallyで取消・awaitしてからsocket、stream、sendを解放します。
    [Theory, InlineData(EDesktopVideoCodec.H264, false), InlineData(EDesktopVideoCodec.Vp9, false), InlineData(EDesktopVideoCodec.H265, false),
     InlineData(EDesktopVideoCodec.H264, true), InlineData(EDesktopVideoCodec.Vp9, true), InlineData(EDesktopVideoCodec.H265, true),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "所有する受信taskをfinallyでawaitしてから借用資源を解放します。")]
    public async Task DelayedSenderReportAndOldFrameDoNotDisconnectVideoAsync(EDesktopVideoCodec codec, bool packetLoss)
    {
        using Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using SemaphoreSlim writes = new(1, 1);
        int frameSize = 30;
        if (codec == EDesktopVideoCodec.Vp9)
        {
            frameSize = 26;
        }
        if (codec == EDesktopVideoCodec.H265)
        {
            frameSize = 31;
        }
        using FrameStream output = new(frameSize * 2 + 32);
        Task receive = DesktopVideo.ForwardAsync(receiver, 0, output, writes, codec, timeout.Token, stop.Token);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[][] packets = [Packet(1, 100, true, codec), Report(now, 100), Packet(2, 100, false, codec), Packet(3, 100, true, codec),
                Report(now.AddSeconds(-10), 200), Packet(4, 200, true, codec), Report(now, 300), Packet(5, 300, false, codec), Packet(6, 300, true, codec)];
            if (packetLoss)
            {
                packets = [Report(now, 100), Packet(3, 100, true, codec), Packet(5, 200, false, codec), Packet(6, 300, true, codec)];
            }
            foreach (byte[] packet in packets)
            {
                await sender.SendToAsync(packet, SocketFlags.None, receiver.LocalEndPoint!, timeout.Token).ConfigureAwait(true);
            }
            await output.Completed.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.False(receive.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(true);
            try
            {
                await receive.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
        byte[] frames = output.ToArray();
        Assert.Equal(frameSize * 2 + 32, frames.Length);
        Assert.Equal(3, frames[frameSize - 1]);
        Assert.Equal(9, BinaryPrimitives.ReadInt32BigEndian(frames.AsSpan(frameSize)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(frames.AsSpan(frameSize + 12)));
        Assert.Equal(6, frames[^1]);
    }

    /// <summary>映像の切替では送信中の一枚を完了し、接続終了では書込みも中止します。</summary>
    /// <returns>ヘッダー送信直後に取消す境界の確認です。</returns>
    [Theory, InlineData(false), InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "受信taskをfinallyで取消・awaitしてから借用資源を解放します。")]
    public async Task CaptureCancellationPreservesTheCurrentFrameAsync(bool closeSession)
    {
        using Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using CancellationTokenSource capture = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        using SemaphoreSlim writes = new(1, 1);
        CancellationTokenSource cancelled = capture;
        int expectedLength = 30;
        if (closeSession)
        {
            cancelled = session;
            expectedLength = DesktopWire.HeaderSize;
        }
        using CancelAfterHeaderStream output = new(cancelled);
        Task receive = DesktopVideo.ForwardAsync(receiver, 0, output, writes, EDesktopVideoCodec.H264, session.Token, capture.Token);
        try
        {
            await sender.SendToAsync(Report(DateTimeOffset.UtcNow, 100), SocketFlags.None, receiver.LocalEndPoint!, timeout.Token).ConfigureAwait(true);
            await sender.SendToAsync(Packet(1, 100, true, EDesktopVideoCodec.H264), SocketFlags.None, receiver.LocalEndPoint!, timeout.Token).ConfigureAwait(true);
            try
            {
                await receive.WaitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (capture.IsCancellationRequested && !timeout.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await capture.CancelAsync().ConfigureAwait(true);
            try
            {
                await receive.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (capture.IsCancellationRequested)
            {
            }
        }
        byte[] frame = output.ToArray();
        Assert.Equal(expectedLength, frame.Length);
        if (!closeSession)
        {
            Assert.Equal(frame.Length - DesktopWire.HeaderSize, DesktopWire.PayloadLength(frame.AsSpan(0, DesktopWire.HeaderSize)));
        }
    }

    /// <summary>ヘッダーが外へ出た時点で、切替または接続終了を再現します。</summary>
    private sealed class CancelAfterHeaderStream(CancellationTokenSource cancellation) : MemoryStream
    {
        /// <summary>取消後の書込みにも通常のStreamと同じトークン判定を適用します。</summary>
        /// <returns>実際の書込みと取消通知です。</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (Length == DesktopWire.HeaderSize)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>識別できる単一NALを一つのRTP packetへ入れます。</summary>
    /// <returns>marker付きpacketです。</returns>
    private static byte[] Packet(ushort sequence, uint timestamp, bool key, EDesktopVideoCodec codec)
    {
        int length = 14;
        if (codec == EDesktopVideoCodec.Vp9)
        {
            length++;
        }
        if (codec == EDesktopVideoCodec.H265)
        {
            length = 15;
        }
        byte[] bytes = new byte[length];
        bytes[0] = 0x80;
        bytes[1] = 0xe0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 42);
        bytes[12] = 0x41;
        if (key)
        {
            bytes[12] = 0x65;
        }
        bytes[13] = (byte)sequence;
        if (codec == EDesktopVideoCodec.Vp9)
        {
            bytes[12] = 12;
            bytes[13] = 0x86;
            if (key)
            {
                bytes[13] = 0x82;
            }
            bytes[14] = (byte)sequence;
        }
        if (codec == EDesktopVideoCodec.H265)
        {
            bytes[12] = 2;
            if (key)
            {
                bytes[12] = 0x26;
            }
            bytes[13] = 1;
            bytes[14] = (byte)sequence;
        }
        return bytes;
    }

    /// <summary>試験のRTP時計をUTCへ対応させます。</summary>
    /// <returns>最小sender reportです。</returns>
    private static byte[] Report(DateTimeOffset time, uint timestamp)
    {
        byte[] bytes = new byte[28];
        bytes[0] = 0x80;
        bytes[1] = 200;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 42);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), checked((uint)(time.ToUnixTimeSeconds() + 2_208_988_800)));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), timestamp);
        return bytes;
    }

    /// <summary>二つの完全なaccess unitの受信だけを通知します。</summary>
    private sealed class FrameStream(int expected) : MemoryStream
    {
        /// <summary>必要な映像を受信したことの通知です。</summary>
        internal TaskCompletionSource Completed 
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>書込み完了後に試験の待機を解放します。</summary>
        /// <returns>実メモリストリームへの書込みです。</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (expected <= Length)
            {
                Completed.TrySetResult();
            }
        }
    }
}
