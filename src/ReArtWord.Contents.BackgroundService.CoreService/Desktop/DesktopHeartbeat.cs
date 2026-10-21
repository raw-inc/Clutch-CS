using System.Buffers.Binary;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>静止画・無音と通信停止を区別する、明示要求された接続だけの生存通知です。</summary>
internal static class DesktopHeartbeat
{
    /// <summary>再利用する一つのmessageを映像・音声と同じ排他で送信します。</summary>
    /// <returns>接続終了までの通知です。</returns>
    internal static async Task RunAsync(Stream stream, SemaphoreSlim send, CancellationTokenSource lifetime, DesktopFault fault, CancellationToken cancellationToken = default)
    {
        byte[] message = new byte[DesktopWire.HeaderSize + 8];
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        long sequence = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long now = (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
                DesktopWire.WriteHeader(message.AsSpan(0, DesktopWire.HeaderSize), 9, 0, 8, false, now);
                BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(DesktopWire.HeaderSize), ++sequence);
                await send.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    send.Release();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            fault.Record("connection", exception);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }
}
