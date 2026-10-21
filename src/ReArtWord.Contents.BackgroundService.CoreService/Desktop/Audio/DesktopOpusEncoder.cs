using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Audio;

/// <summary>固定長PCM蓄積と20ms Opus packetの時計・所有権を管理します。</summary>
internal sealed class DesktopOpusEncoder : IDisposable
{
    private readonly IOpusEncoder encoder;
    private readonly float[] samples = new float[1920];
    private readonly byte[] packet = new byte[DesktopWire.HeaderSize + 8 + 1275];
    private readonly Action<ReadOnlyMemory<byte>> publish;
    private int count;
    private long timestamp;
    private long expected;
    private bool reset = true;
    private bool dirty;
    private bool ended;
    private long capturedFrames;
    private long emittedFrames;

    /// <summary>選択済みのCBR音声と有限の圧縮領域を確保します。</summary>
    internal DesktopOpusEncoder(int bitrateKbps, Action<ReadOnlyMemory<byte>> publish)
    {
        new DesktopAudioSettings(1, bitrateKbps).Validate();
        this.publish = publish;
        encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitrateKbps * 1000;
        encoder.UseVBR = false;
        encoder.Complexity = 5;
    }

    /// <summary>任意のWASAPI packetを、時間を保持した20ms単位へまとめます。</summary>
    internal void Append(ReadOnlySpan<float> input, long capturedAt, bool discontinuity)
    {
        if (input.Length % 2 != 0 || 16384 < input.Length || capturedAt < 0)
        {
            throw new InvalidDataException("Invalid desktop PCM packet.");
        }
        if (ended || discontinuity || expected != 0 && 20000 < Math.Abs(capturedAt - expected))
        {
            FlushTail();
            encoder.ResetState();
            reset = true;
            capturedFrames = emittedFrames = 0;
            ended = false;
        }
        expected = capturedAt + input.Length / 2 * 1_000_000L / 48000;
        capturedFrames += input.Length / 2;
        dirty |= input.Length != 0;
        int offset = 0;
        while (offset < input.Length)
        {
            if (count == 0)
            {
                timestamp = capturedAt + offset / 2 * 1_000_000L / 48000;
            }
            int take = Math.Min(samples.Length - count, input.Length - offset);
            input.Slice(offset, take).CopyTo(samples.AsSpan(count));
            count += take;
            offset += take;
            if (count == samples.Length)
            {
                Emit();
            }
        }
    }

    /// <summary>入力が止まった際も最後の短い音声を無期限に保持しません。</summary>
    internal void FlushTail()
    {
        if (!dirty)
        {
            return;
        }
        int padding = 0;
        if (count != 0)
        {
            padding = (samples.Length - count) / 2;
            samples.AsSpan(count).Clear();
            Emit(encoder.Lookahead <= padding);
        }
        if (padding < encoder.Lookahead)
        {
            Array.Clear(samples);
            timestamp = expected + padding * 1_000_000L / 48000;
            Emit(true);
        }
        dirty = false;
        ended = true;
    }

    /// <summary>次の呼出しまでだけ有効な固定領域で送信し、codec遅延を時刻へ反映します。</summary>
    private void Emit(bool end = false)
    {
        int length = encoder.Encode(samples, 960, packet.AsSpan(DesktopWire.HeaderSize + 8), 1275);
        int skip = checked((int)Math.Max(0, encoder.Lookahead - emittedFrames));
        int valid = checked((int)(Math.Min(emittedFrames + 960, capturedFrames + encoder.Lookahead) - emittedFrames - skip));
        long start = Math.Max(0, timestamp + (skip - encoder.Lookahead) * 1_000_000L / 48000);
        DesktopWire.WriteHeader(packet, 10, 0, length + 8, reset, start);
        if (end)
        {
            int flags = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(12)) | 2;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), flags);
        }
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(DesktopWire.HeaderSize), valid);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(DesktopWire.HeaderSize + 4), skip);
        publish(packet.AsMemory(0, DesktopWire.HeaderSize + 8 + length));
        emittedFrames += 960;
        count = 0;
        reset = false;
    }

    /// <summary>接続が所有するcodecだけを解放します。</summary>
    public void Dispose() => encoder.Dispose();
}
