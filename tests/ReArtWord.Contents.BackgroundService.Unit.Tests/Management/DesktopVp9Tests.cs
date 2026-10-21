using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>VP9の断片、欠落、上限と固定領域の再利用を確認します。</summary>
public sealed class DesktopVp9Tests
{
    /// <summary>FFmpegのB/E descriptorを除き、keyとdeltaを同じbyte列へ戻します。</summary>
    [Fact]
    public void FragmentsAndReferenceRecoveryAreBounded()
    {
        Vp9RtpAssembler assembler = new(new byte[32]);
        Assert.False(assembler.Append(Packet(1, 100, 8, [0x82, 1])));
        Assert.True(assembler.Append(Packet(2, 100, 4, [2, 3])));
        Assert.True(assembler.KeyFrame);
        Assert.Equal(new byte[] { 0x82, 1, 2, 3 }, assembler.Frame.ToArray());
        Assert.True(assembler.Append(Packet(3, 200, 12, [0x86, 4])));
        Assert.False(assembler.KeyFrame);
        Assert.False(assembler.Append(Packet(4, 300, 8, [0x86, 5])));
        Assert.False(assembler.Append(Packet(6, 300, 4, [6])));
        Assert.False(assembler.Append(Packet(7, 400, 12, [0x86, 7])));
        Assert.True(assembler.Append(Packet(8, 500, 12, [0x82, 8])));
        byte[] malformed = Packet(9, 600, 12, [0x82, 9]);
        malformed[0] |= 0x10;
        Assert.False(assembler.Append(malformed));
        Assert.False(assembler.Append(Packet(10, 700, 12, [0x86, 10])));
        Assert.True(assembler.Append(Packet(11, 800, 12, [0x82, 11])));
    }

    /// <summary>未対応layer・profile、marker不一致、容量超過を転送しません。</summary>
    [Fact]
    public void UnsupportedAndOversizedPayloadsAreRejected()
    {
        Vp9RtpAssembler assembler = new(new byte[2]);
        Assert.False(assembler.Append(Packet(1, 1, 0x8c, [0x82, 1])));
        Assert.False(assembler.Append(Packet(2, 2, 12, [0x92, 1])));
        Assert.False(assembler.Append(Packet(3, 3, 12, [0x82, 1, 2])));
        byte[] mismatch = Packet(4, 4, 8, [0x82, 1]);
        mismatch[1] |= 0x80;
        Assert.False(assembler.Append(mismatch));
        Assert.True(assembler.Append(Packet(5, 5, 12, [0x82, 2])));
    }

    /// <summary>連番wrapを含め、定常のpacket復元でmanaged allocationが発生しません。</summary>
    [Fact]
    public void ReassemblyDoesNotAllocatePerPacket()
    {
        Vp9RtpAssembler assembler = new(new byte[32]);
        byte[] packet = Packet(65535, 1, 12, [0x82, 1]);
        Assert.True(assembler.Append(packet));
        for (int index = 0; index < 100; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)index);
            Assert.True(assembler.Append(packet));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool complete = true;
        for (int index = 100; index < 10000; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)index);
            complete &= assembler.Append(packet);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(complete);
        Assert.Equal(0, allocated);
    }

    /// <summary>FFmpegと同じ一byte descriptorを付けたRTP packetです。</summary>
    /// <returns>借用領域へ入力するpacketです。</returns>
    private static byte[] Packet(ushort sequence, uint timestamp, byte descriptor, byte[] payload)
    {
        byte[] packet = new byte[13 + payload.Length];
        packet[0] = 0x80;
        packet[1] = 96;
        if ((descriptor & 4) != 0)
        {
            packet[1] |= 0x80;
        }
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        packet[12] = descriptor;
        payload.CopyTo(packet, 13);
        return packet;
    }
}
