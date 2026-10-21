using System.Buffers.Binary;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>映像の送信開始時刻と時計差の応答を、元のフレームと同じTCPへ順序付きで送ります。</summary>
internal sealed class DesktopStreamTiming(Stream network, SemaphoreSlim writes)
{
    private bool enabled;

    private static long Now => (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));

    /// <summary>ブラウザーの単調時刻をそのまま返し、Coreでの受信・送信時刻を加えます。</summary>
    /// <returns>現在の一回の時計差照会への応答です。</returns>
    internal async Task ReplyAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        long received = Now;
        int sequence = BinaryPrimitives.ReadInt32BigEndian(input.Span[4..]);
        long sent = BinaryPrimitives.ReadInt64BigEndian(input.Span[8..]);
        if (sequence < 1 || sent < 0 || input.Span[16..].ContainsAnyExcept((byte)0))
        {
            throw new InvalidDataException("Invalid desktop clock request.");
        }
        byte[] reply = new byte[DesktopWire.HeaderSize + 24];
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(24), sequence);
        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(32), sent);
        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(40), received);
        if (!await writes.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // 映像の送信待ちへ計測を追加せず、入力と生存通知の受付を維持します。
            return;
        }
        try
        {
            Volatile.Write(ref enabled, true);
            DesktopWire.WriteHeader(reply, 13, 0, 24, false, Now);
            await network.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>映像だけに送信開始の印を前置し、取得時刻と元のペイロードを維持します。</summary>
    /// <returns>workerからの全フレームの中継です。</returns>
    internal async Task ForwardAsync(Stream worker, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[DesktopWire.HeaderSize];
        byte[] marker = new byte[DesktopWire.HeaderSize + 8];
        byte[] buffer = GC.AllocateUninitializedArray<byte>(65536);
        while (!cancellationToken.IsCancellationRequested)
        {
            await worker.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int remaining = DesktopWire.PayloadLength(header);
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref enabled) && BinaryPrimitives.ReadInt32BigEndian(header) == 2)
                {
                    int monitor = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
                    DesktopWire.WriteHeader(marker, 14, monitor, 8, false, Now);
                    header.AsSpan(16, 8).CopyTo(marker.AsSpan(24));
                    await network.WriteAsync(marker, cancellationToken).ConfigureAwait(false);
                }
                await network.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                while (0 < remaining)
                {
                    int count = Math.Min(remaining, buffer.Length);
                    await worker.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    await network.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    remaining -= count;
                }
            }
            finally
            {
                writes.Release();
            }
        }
    }
}
