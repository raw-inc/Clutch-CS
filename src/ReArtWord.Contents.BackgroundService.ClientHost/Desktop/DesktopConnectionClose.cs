using System.Net.WebSockets;
using System.Net.Sockets;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Desktop;

/// <summary>通知可能な接続には理由付き終了を送り、例外本文や機密値を公開しません。</summary>
internal static class DesktopConnectionClose
{
    /// <summary>送信側のFINを先に通知し、残る映像を有限時間だけ排出して正常切断をCoreへ届けます。</summary>
    /// <returns>受信側のEOF、または上流が終了しない場合の待機上限です。</returns>
    internal static async Task DrainUpstreamAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            byte[] buffer = new byte[65536];
            while (await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) != 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // 終了済みのWebSocketを待たせず、切断確認ができないTCPも呼出元のusingで閉じます。
        }
    }

    /// <summary>中継の送受信終了後、有限時間で終了通知を一度だけ試みます。</summary>
    /// <returns>通知の完了、または通知不能な接続の破棄です。</returns>
    internal static async Task SendAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken cancellationToken = default)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Abort();
        }
    }
}
