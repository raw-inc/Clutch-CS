using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>破損時の復号境界と有限メモリでの映像輸送を確認します。</summary>
public sealed class DesktopProtocolTests
{
    /// <summary>断片NALをmarkerまで一つの借用フレームへ復元します。</summary>
    [Fact]
    public void FragmentedIdrAndSubsequentDeltaAreReassembled()
    {
        H26xRtpAssembler assembler = new(new byte[64]);
        Assert.False(assembler.Append(Packet(1, 100, false, [0x7c, 0x85, 10, 20])));
        Assert.True(assembler.Append(Packet(2, 100, true, [0x7c, 0x45, 30, 40])));
        Assert.True(assembler.KeyFrame);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 10, 20, 30, 40 }, assembler.Frame.ToArray());
        Assert.True(assembler.Append(Packet(3, 200, true, [0x41, 50])));
        Assert.False(assembler.KeyFrame);
    }

    /// <summary>欠落後の参照フレームを通さず次のIDRで再開します。</summary>
    [Fact]
    public void PacketLossRequiresAnotherIdr()
    {
        H26xRtpAssembler assembler = new(new byte[64]);
        Assert.True(assembler.Append(Packet(1, 100, true, [0x65, 1])));
        Assert.False(assembler.Append(Packet(2, 200, false, [0x7c, 0x81, 2])));
        Assert.False(assembler.Append(Packet(4, 200, true, [0x7c, 0x41, 3])));
        Assert.False(assembler.Append(Packet(5, 300, true, [0x41, 4])));
        Assert.True(assembler.Append(Packet(6, 400, true, [0x65, 5])));
        Assert.False(assembler.Append(Packet(7, 500, true, [0x78])));
        Assert.False(assembler.Append(Packet(8, 600, true, [0x41, 6])));
        Assert.True(assembler.Append(Packet(9, 700, true, [0x65, 7])));
    }

    /// <summary>上限超過や不正なextensionをフレームとして公開しません。</summary>
    [Fact]
    public void OverflowAndMalformedExtensionAreRejected()
    {
        H26xRtpAssembler assembler = new(new byte[6]);
        Assert.False(assembler.Append(Packet(1, 100, true, [0x65, 1, 2])));
        byte[] packet = Packet(2, 200, true, [0x65, 1]);
        packet[0] |= 0x10;
        Assert.False(assembler.Append(packet));
        Assert.False(assembler.Append(Packet(3, 300, true, [0x41, 1])));
        Assert.True(assembler.Append(Packet(4, 400, true, [0x65, 1])));
    }

    /// <summary>連番wrapと集約NALを扱い、ウォームアップ後に割当てません。</summary>
    [Theory, InlineData(false), InlineData(true)]
    public void ReusedBuffersHaveNoPerPacketManagedAllocation(bool hevc)
    {
        H26xRtpAssembler assembler = new(new byte[64], hevc);
        byte[] payload = [0x78, 0, 2, 0x67, 0x42, 0, 2, 0x65, 1];
        if (hevc)
        {
            payload = [0x60, 1, 0, 3, 0x42, 1, 1, 0, 3, 0x26, 1, 2];
        }
        byte[] packet = Packet(65535, 1, true, payload);
        Assert.True(assembler.Append(packet));
        for (int index = 0; index < 100; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)index);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)index + 2);
            Assert.True(assembler.Append(packet));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int index = 100; index < 10000; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)index);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)index + 2);
            accepted &= assembler.Append(packet);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(accepted);
        Assert.Equal(0, allocated);
    }

    /// <summary>HEVCのFUをAnnex Bへ復元し、欠落後は次のIRAPを待ちます。</summary>
    [Fact]
    public void HevcFragmentsAndLossRecoverAtNextKeyFrame()
    {
        H26xRtpAssembler assembler = new(new byte[128], true);
        Assert.False(assembler.Append(Packet(1, 100, false, [0x62, 1, 0x93, 10, 20])));
        Assert.True(assembler.Append(Packet(2, 100, true, [0x62, 1, 0x53, 30, 40])));
        Assert.True(assembler.KeyFrame);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x26, 1, 10, 20, 30, 40 }, assembler.Frame.ToArray());
        Assert.True(assembler.Append(Packet(3, 200, true, [2, 1, 50])));
        Assert.False(assembler.KeyFrame);
        Assert.False(assembler.Append(Packet(4, 300, false, [0x62, 1, 0x81, 60])));
        Assert.False(assembler.Append(Packet(6, 300, true, [0x62, 1, 0x41, 70])));
        Assert.False(assembler.Append(Packet(7, 400, true, [2, 1, 80])));
        Assert.True(assembler.Append(Packet(8, 500, true, [0x28, 1, 90])));
    }

    /// <summary>入れ子AP、壊れたheader、fragment identityの変化と上限超過を拒否します。</summary>
    [Fact]
    public void HevcMalformedPayloadsAreNotPublished()
    {
        H26xRtpAssembler assembler = new(new byte[32], true);
        Assert.False(assembler.Append(Packet(1, 100, true, [0x60, 1, 0, 2, 0x60, 1])));
        Assert.False(assembler.Append(Packet(2, 200, true, [0x26, 0, 10])));
        Assert.False(assembler.Append(Packet(3, 300, false, [0x62, 1, 0x93, 10])));
        Assert.False(assembler.Append(Packet(4, 300, true, [0x62, 2, 0x53, 20])));
        Assert.False(assembler.Append(Packet(5, 400, true, [0x62, 1, 0xd3, 10])));
        Assert.True(assembler.Append(Packet(6, 500, true, [0x26, 1, 30])));
        H26xRtpAssembler small = new(new byte[6], true);
        Assert.False(small.Append(Packet(1, 100, true, [0x26, 1, 30])));
    }

    /// <summary>frame上限を受信前に検査し、ヘッダー値を保持します。</summary>
    [Fact]
    public void WireRejectsOversizedOrUnknownMessages()
    {
        byte[] header = new byte[DesktopWire.HeaderSize];
        DesktopWire.WriteHeader(header, 2, 1, 1024, true, 123456);
        Assert.Equal(1024, DesktopWire.PayloadLength(header));
        Assert.Equal(123456, BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16)));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), DesktopWire.MaximumFrameBytes + 1);
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
        DesktopWire.WriteHeader(header, 10, 0, 1, false, 0);
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
    }

    /// <summary>RFC6184の固定headerを持つローカルpacketを作ります。</summary>
    /// <returns>指定した連番とtimestampのpacketです。</returns>
    private static byte[] Packet(ushort sequence, uint timestamp, bool marker, byte[] payload)
    {
        byte[] packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = 96;
        if (marker)
        {
            packet[1] |= 0x80;
        }
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        payload.CopyTo(packet, 12);
        return packet;
    }
}
