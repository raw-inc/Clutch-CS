using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Terminal;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Terminal;

/// <summary>端末を解釈せず、有限領域でTCPとWebSocketを接続します。</summary>
internal static class TerminalRelay
{
    /// <summary>どちらかの切断で両方向のI/Oを解放します。</summary>
    /// <returns>中継の終了です。</returns>
    // 抑制理由: finallyで取消後に全タスクの完了をawaitします。
    [SuppressMessage("Reliability", "CA2025", Justification = "finallyで両方向のタスクを回収します。")]
    internal static async Task RunAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task output = OutputAsync(stream, socket, linked.Token), input = InputAsync(stream, socket, linked.Token);
        try
        {
            await Task.WhenAny(output, input).ConfigureAwait(false);
        }

        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(output, input).ConfigureAwait(false);
        }
    }

    /// <summary>Coreからの一フレームを一つのブラウザーメッセージへ中継します。</summary>
    /// <returns>出力方向の終了です。</returns>
    private static async Task OutputAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[TerminalWire.MaximumMessageBytes + 4];
        while (!cancellationToken.IsCancellationRequested)
        {
            int length = await TerminalWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            await socket.SendAsync(buffer.AsMemory(4, length), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>断片化を受けても上限を越えず、元の順序で入力を中継します。</summary>
    /// <returns>入力方向の終了です。</returns>
    private static async Task InputAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[16384];
        int offset = 4;
        while (!cancellationToken.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            offset += result.Count;
            if (result.MessageType != WebSocketMessageType.Text || offset == buffer.Length && !result.EndOfMessage)
            {
                throw new InvalidDataException("Invalid terminal input frame.");
            }

            if (result.EndOfMessage)
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer, offset - 4);
                await stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                offset = 4;
            }
        }
    }
}

