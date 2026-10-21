using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;

/// <summary>カーソル種類の変化だけを有界messageで送り、映像fpsから独立して追従します。</summary>
internal static class DesktopCursor
{
    /// <summary>初期状態と種類・画面の変更だけを、既存streamの排他内で送ります。</summary>
    /// <returns>接続終了までの通知です。</returns>
    internal static async Task RunAsync(Func<(int Monitor, EDesktopCursorShape Shape)> read, Stream stream, SemaphoreSlim send,
        CancellationTokenSource lifetime, DesktopFault fault, CancellationToken cancellationToken = default)
    {
        byte[] message = new byte[DesktopWire.HeaderSize + 4];
        (int Monitor, EDesktopCursorShape Shape)? previous = null;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                (int monitor, EDesktopCursorShape shape) = read();
                if (previous == (monitor, shape))
                {
                    continue;
                }
                DesktopWire.WriteHeader(message.AsSpan(0, DesktopWire.HeaderSize), 11, monitor, 4, false, 0);
                BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(DesktopWire.HeaderSize), (int)shape);
                await send.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    send.Release();
                }
                previous = (monitor, shape);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            fault.Record("cursor", exception);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }
}
