using System.Buffers.Binary;
using Concentus;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Audio;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実Opusの符号化・途中復号・末尾・再開と有限な通信サイズを確認します。</summary>
public sealed class DesktopOpusTests
{
    /// <summary>任意の取得単位でも、遅延・末尾paddingを再生サンプルに混ぜません。</summary>
    [Theory, InlineData(16), InlineData(24), InlineData(32), InlineData(48), InlineData(64), InlineData(96), InlineData(128), InlineData(192), InlineData(256)]
    public void ActualPacketsPreserveSampleCountAndResumeAtEveryRate(int bitrate)
    {
        List<byte[]> packets = [];
        using DesktopOpusEncoder encoder = new(bitrate, packet => packets.Add(packet.ToArray()));
        float[] source = new float[9600];
        for (int index = 0; index < source.Length; index++)
        {
            source[index] = 0.25f * MathF.Sin(index / 2 * 2 * MathF.PI * 440 / 48000);
        }
        int offset = 0;
        foreach (int frames in new[] { 13, 480, 967, 240, 1311, 1789 })
        {
            encoder.Append(source.AsSpan(offset * 2, frames * 2), 1_000_000 + offset * 1_000_000L / 48000, false);
            offset += frames;
        }
        encoder.FlushTail();
        int originalPackets = packets.Count;
        encoder.FlushTail();
        Assert.Equal(originalPackets, packets.Count);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(packets[^1].AsSpan(12)) & 2);
        using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        float[] output = new float[1920];
        long previousTime = 0;
        int total = 0;
        double energy = 0;
        foreach (byte[] packet in packets)
        {
            Assert.Equal(packet.Length - DesktopWire.HeaderSize, DesktopWire.PayloadLength(packet.AsSpan(0, DesktopWire.HeaderSize)));
            Assert.Equal(bitrate * 5 / 2, packet.Length - 32);
            int frames = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(24));
            int skip = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(28));
            long timestamp = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(16));
            Assert.InRange(timestamp, previousTime + 1, long.MaxValue);
            previousTime = timestamp;
            Assert.Equal(960, decoder.Decode(packet.AsSpan(32), output, 960, false));
            Assert.InRange(frames, 1, 960 - skip);
            foreach (float sample in output.AsSpan(skip * 2, frames * 2))
            {
                Assert.True(float.IsFinite(sample));
                energy += sample * sample;
            }
            total += frames;
        }
        Assert.Equal(4800, total);
        Assert.InRange(energy / (total * 2), 0.005, 0.2);
        using IOpusDecoder joined = OpusCodecFactory.CreateDecoder(48000, 2);
        Assert.Equal(960, joined.Decode(packets[2].AsSpan(32), output, 960, false));
        encoder.Append(source.AsSpan(0, 100), 5_000_000, false);
        encoder.FlushTail();
        byte[] resumed = packets[originalPackets];
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(resumed.AsSpan(12)));
        Assert.Equal(5_000_000, BinaryPrimitives.ReadInt64BigEndian(resumed.AsSpan(16)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32BigEndian(resumed.AsSpan(24)));
    }

    /// <summary>明示された方式・レート以外を符号化前に拒否します。</summary>
    [Theory, InlineData(-1, 0), InlineData(2, 96), InlineData(0, 96), InlineData(1, 0), InlineData(1, 17), InlineData(1, 100000)]
    public void InvalidAudioSelectionsAreRejected(int codec, int bitrate)
        => Assert.Throws<ArgumentException>(() => new DesktopAudioSettings(codec, bitrate).Validate());

    /// <summary>Opus専用packetのprefixと圧縮本文の長さ上限を中継前に確認します。</summary>
    [Theory, InlineData(1, false), InlineData(8, false), InlineData(9, true), InlineData(1283, true), InlineData(1284, false)]
    public void OpusWireLengthIsBounded(int length, bool valid)
    {
        byte[] header = new byte[DesktopWire.HeaderSize];
        DesktopWire.WriteHeader(header, 10, 0, length, false, 0);
        if (valid)
        {
            Assert.Equal(length, DesktopWire.PayloadLength(header));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
        }
    }
}
