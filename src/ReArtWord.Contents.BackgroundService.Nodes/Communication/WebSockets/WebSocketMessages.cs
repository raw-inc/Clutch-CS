using System.Net.WebSockets;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;

/// <summary>断片を一メッセージへ組み立て、受付前停止と受付後の期限を分けます。</summary>
internal static class WebSocketMessages
{
    /// <summary>先頭断片後は正常停止で中断せず、その一件の期限まで読取りを続けます。</summary>
    /// <returns>完全メッセージ、正常なcloseならnullです。</returns>
    internal static async Task<WebSocketMessage?> ReadAsync(WebSocket socket, Func<NodeExecutionContext> capture, CancellationToken stop,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[8192];
        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
        ValueWebSocketReceiveResult fragment = await socket.ReceiveAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
        if (fragment.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }
        NodeExecutionContext context = capture();
        NodeSettings settings = new(context.Settings);
        using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
        using MemoryStream body = new();
        WebSocketMessageType type = fragment.MessageType;
        int maximum = settings.ReadInt32("maxBytes", 1048576);
        bool complete = false;
        while (!complete)
        {
            if (type != fragment.MessageType || maximum < body.Length + fragment.Count)
            {
                throw new InvalidDataException("websocket.messageLimitOrType");
            }
            body.Write(buffer.AsSpan(0, fragment.Count));
            complete = fragment.EndOfMessage;
            if (!complete)
            {
                fragment = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            }
        }
        byte[] bytes = body.ToArray();
        NodeValue value = NodeValue.FromBytes(bytes);
        if (type == WebSocketMessageType.Text)
        {
            value = NodeValue.FromObject(new UTF8Encoding(false, true).GetString(bytes));
        }
        await CommunicationTrace.WriteAsync(context, "websocket", false, bytes, CancellationToken.None).ConfigureAwait(false);
        return new(context, value, bytes, type);
    }

    /// <summary>送信前拒否と、送信開始後の確認不能を区別します。</summary>
    /// <returns>ローカル送信の確認結果です。</returns>
    internal static async Task<NodeResult> SendAsync(WebSocket socket, NodeValue value, NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        bool sending = false;
        try
        {
            byte[] bytes = Encode(value);
            int maximum = new NodeSettings(context.Settings).ReadInt32("maxBytes", 1048576);
            if (maximum < bytes.Length || socket.State != WebSocketState.Open)
            {
                return NodeResult.Failure("websocket.sendUnavailable", "送信上限または接続状態を確認してください。", EOutcome.NotExecuted);
            }
            cancellationToken.ThrowIfCancellationRequested();
            WebSocketMessageType type = WebSocketMessageType.Text;
            if (value.Kind == EValueKind.Binary)
            {
                type = WebSocketMessageType.Binary;
            }
            sending = true;
            await socket.SendAsync(bytes.AsMemory(), type, true, cancellationToken).ConfigureAwait(false);
            await CommunicationTrace.WriteAsync(context, "websocket", true, bytes, CancellationToken.None).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(new { bytesSent = bytes.Length, confirmation = "localSend" }));
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException or OperationCanceledException or EncoderFallbackException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (sending)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("websocket.sendUnconfirmed", "送信完了を確認できません。", outcome);
        }
    }

    /// <summary>Binaryはバイト列、JSON文字列は文字列、それ以外のJSONはJSON本文にします。</summary>
    /// <returns>型を変換せずに送信する本文です。</returns>
    internal static byte[] Encode(NodeValue value)
    {
        if (value.Kind == EValueKind.Binary)
        {
            return value.Binary.ToArray();
        }
        return new UTF8Encoding(false, true).GetBytes(value.GetString() ?? value.Json.GetRawText());
    }

    /// <summary>接続の終了を有限時間だけ試み、以後はこのソケットだけを解放します。</summary>
    /// <returns>ローカル接続の解放完了です。</returns>
    internal static async Task CloseAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "completed", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Abort();
        }
    }
}
