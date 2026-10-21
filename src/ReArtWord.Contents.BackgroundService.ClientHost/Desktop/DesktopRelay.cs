using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Desktop;

/// <summary>圧縮フレームを有限の再利用バッファで中継します。</summary>
internal static class DesktopRelay
{
    /// <summary>片方向の終了で両方向を停止します。</summary>
    /// <returns>すべてのI/Oの終了です。</returns>
    // 抑制理由: linked tokenを解放する前にfinallyで両taskをawaitします。
    [SuppressMessage("Reliability", "CA2025", Justification = "両taskはfinallyで回収します。")]
    internal static async Task RunAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim writes = new(1, 1);
        Task video = VideoAsync(stream, socket, lifetime.Token);
        Task input = InputAsync(stream, socket, writes, lifetime.Token);
        Task heartbeat = HeartbeatAsync(stream, writes, lifetime.Token);
        try
        {
            Task ended = await Task.WhenAny(video, input, heartbeat).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.NormalClosure, "Desktop.Closed", CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.ProtocolError, "Desktop.InvalidData", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            // ReceiveAsyncの取消しはWebSocket自体を破棄するため、先に終了理由を通知します。
            await DesktopConnectionClose.SendAsync(socket, WebSocketCloseStatus.InternalServerError, "Desktop.UpstreamDisconnected", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(video, input, heartbeat).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException or InvalidDataException)
            {
                // 先に確定した終了を、残り方向の取消し・破棄による例外で上書きしません。
            }
        }
    }

    /// <summary>一つのaccess unitを一つのWebSocket messageとして送ります。</summary>
    /// <returns>映像方向の終了です。</returns>
    private static async Task VideoAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[DesktopWire.HeaderSize];
        byte[] buffer = GC.AllocateUninitializedArray<byte>(65536);
        while (!cancellationToken.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int remaining = DesktopWire.PayloadLength(header);
            await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Binary, false, cancellationToken).ConfigureAwait(false);
            while (0 < remaining)
            {
                int count = Math.Min(remaining, buffer.Length);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
                await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, remaining == 0, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>固定長の操作または有限長のテキストを一件ずつ受理し、分割された本文を復元します。</summary>
    /// <returns>入力方向の終了です。</returns>
    private static async Task InputAsync(Stream stream, WebSocket socket, SemaphoreSlim writes, CancellationToken cancellationToken = default)
    {
        byte[] input = new byte[DesktopWire.InputSize + DesktopClipboardWire.MaximumTextBytes];
        int offset = 0;
        int expected = DesktopWire.InputSize;
        while (!cancellationToken.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult received = await socket.ReceiveAsync(input.AsMemory(offset, expected - offset), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            offset += received.Count;
            if (offset == DesktopWire.InputSize)
            {
                expected = DesktopClipboardWire.InputLength(input.AsSpan(0, DesktopWire.InputSize));
            }
            if (received.MessageType != WebSocketMessageType.Binary || (offset == expected && !received.EndOfMessage))
            {
                throw new InvalidDataException("Invalid desktop input message.");
            }
            if (received.EndOfMessage)
            {
                if (offset != expected)
                {
                    throw new InvalidDataException("Invalid desktop input size.");
                }
                await WriteAsync(stream, writes, input.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                offset = 0;
                expected = DesktopWire.InputSize;
            }
        }
    }

    /// <summary>Core側がネットワーク断をOSのTCP timeoutより前に確定できるよう通知します。</summary>
    /// <returns>接続寿命内の通知です。</returns>
    private static async Task HeartbeatAsync(Stream stream, SemaphoreSlim writes, CancellationToken cancellationToken = default)
    {
        byte[] heartbeat = new byte[DesktopWire.InputSize];
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync(stream, writes, heartbeat, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>入力一件と生存通知を直列化し、テキストの途中へ別入力を混入させません。</summary>
    /// <returns>一メッセージの送信です。</returns>
    private static async Task WriteAsync(Stream stream, SemaphoreSlim writes, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }
}
