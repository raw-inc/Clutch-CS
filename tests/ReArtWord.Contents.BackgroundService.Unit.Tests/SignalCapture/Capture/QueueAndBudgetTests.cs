using System.Globalization;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Tcp;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.Tests;

/// <summary>過負荷時の欠落計数と HTTP の総メモリ上限を検証します。</summary>
public sealed class QueueAndBudgetTests
{
    /// <summary>古い原本から除外して最新を保持し、破棄数を累計します。</summary>
    [Fact]
    public void RetainsNewestPacketsWithinBothLimits()
    {
        var queue = new PacketQueue(10, 256);
        for (int i = 0; i < 20; i++)
        {
            queue.Enqueue(new QueuedPacket(1, "demo", new PacketRecord { Data = new byte[64], Index = i }));
        }

        Assert.Equal(16, queue.Dropped);
        Assert.Equal(16, queue.Read()?.Packet.Index);
        Assert.Equal(17, queue.Read()?.Packet.Index);
        queue.Clear();
        Assert.Equal(18, queue.Dropped);
        Assert.Null(queue.Read());
        queue.Reset();
        Assert.Equal(0, queue.Dropped);
    }

    /// <summary>バイト上限に余裕があっても件数を越えて保持しません。</summary>
    [Fact]
    public void BoundsConcurrentArrivalsByCount()
    {
        var queue = new PacketQueue(32, 1024 * 1024);
        Parallel.For(0, 10000, index => queue.Enqueue(new QueuedPacket(1, "demo", new PacketRecord { Data = new byte[8], Index = index })));
        int retained = 0;
        while (queue.Read() is not null)
        {
            retained++;
        }
        Assert.Equal(32, retained);
        Assert.Equal(9968, queue.Dropped);
    }

    /// <summary>多数の取得元・接続があっても TCP 再構成の合計容量を制限します。</summary>
    [Fact]
    public void BoundsHttpReassemblyAcrossSources()
    {
        var tcp = new TcpReassembler(4096);
        for (int i = 0; i < 100; i++)
        {
            var packet = new PacketRecord
            {
                Data = new byte[2048],
                Transport = "TCP",
                CaptureInterface = i.ToString(CultureInfo.InvariantCulture),
                Source = "192.0.2.1",
                Destination = "192.0.2.2",
                SourcePort = 40000,
                DestinationPort = 80,
                PayloadLength = 2048,
                Sequence = 1
            };
            "GET / HTTP/1.1\r\nX-Long: "u8.CopyTo(packet.Data);
            Array.Fill(packet.Data, (byte)'a', 24, packet.Data.Length - 24);
            tcp.Process(packet);
            Assert.InRange(tcp.RetainedBytes, 1, 4096);
        }

        tcp.Clear();
        Assert.Equal(0, tcp.RetainedBytes);
    }
}
