using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.ManagementTransport.State;

namespace ReArtWord.Contents.BackgroundService.ClientHost.States;

/// <summary>Stateの購読と表示変更を、既存のWebポート上のWebSocketへ中継します。</summary>
internal static class StateStreamRelay
{
    /// <summary>接続のどちらかが終了したら、もう一方の待機も確実に取り消します。</summary>
    /// <returns>双方向の中継の終了です。</returns>
    internal static async Task RunAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task outbound = SendAsync(stream, socket, lifetime.Token);
        Task inbound = ReceiveAsync(socket, stream, lifetime.Token);
        try
        {
            Task completed = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>有限なフレームを同じ接続へ順番に送り、送信待ちにも期限を設けます。</summary>
    /// <returns>Core送信側の終了です。</returns>
    private static async Task SendAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[StateStreamWire.MaximumBytes + 4];
        while (!cancellationToken.IsCancellationRequested)
        {
            int length = await StateStreamWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0)
            {
                return;
            }
            using CancellationTokenSource sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sending.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.SendAsync(buffer.AsMemory(4, length), WebSocketMessageType.Text, true, sending.Token).ConfigureAwait(false);
        }
    }

    /// <summary>初回購読だけを送り、追加送信は別接続への切替として扱わせます。</summary>
    /// <returns>ブラウザー受信側の終了です。</returns>
    private static async Task ReceiveAsync(WebSocket socket, Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[StateStreamWire.MaximumBytes];
        int used = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            used += received.Count;
            if (received.MessageType != WebSocketMessageType.Text || buffer.Length <= used)
            {
                throw new InvalidDataException("Invalid state subscription request.");
            }
            if (received.EndOfMessage)
            {
                await StateStreamWire.WriteAsync(stream, buffer.AsMemory(0, used), cancellationToken).ConfigureAwait(false);
                used = 0;
            }
        }
    }
}
