using System.Net.WebSockets;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;

/// <summary>一接続の受信と返信を保持し、正常停止時も受付済み返信を待ちます。</summary>
internal sealed class WebSocketConversation(NodeSourceContext context, CommunicationReplyRegistry replies, Func<bool> admit, Action complete)
{
    /// <summary>一件の問題はこの接続へ限定し、サーバーの待受を終了しません。</summary>
    /// <returns>この接続の処理完了です。</returns>
    internal async Task RunAsync(WebSocket socket, CancellationToken stop, CancellationToken cancellationToken = default)
    {
        NodeExecutionContext? acquired = null;
        try
        {
            while (!stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                acquired = null;
                WebSocketMessage? message = await WebSocketMessages.ReadAsync(socket, () => acquired = context.BeginItem(), stop, cancellationToken).ConfigureAwait(false);
                if (message is null || !admit())
                {
                    return;
                }
                acquired = null;
                try
                {
                    await EmitAsync(socket, message, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    complete();
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or OperationCanceledException or DecoderFallbackException or ReplyCapacityException)
        {
            if (acquired is not null && !cancellationToken.IsCancellationRequested && admit())
            {
                await context.EmitAsync(acquired, NodeResult.Failure("websocket.messageFailed", "一メッセージを取得できませんでした。"), cancellationToken).ConfigureAwait(false);
                complete();
            }
        }
        finally
        {
            await WebSocketMessages.CloseAsync(socket, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>取得したメッセージと任意の一回返信を別の結果として扱います。</summary>
    /// <returns>通知と、選択された返信の完了です。</returns>
    private async Task EmitAsync(WebSocket socket, WebSocketMessage message, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(message.Context.Settings);
        string mode = settings.ReadText("replyMode", "none")!;
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal)
        {
            ["value"] = message.Value,
            ["messageType"] = NodeValue.FromObject(message.Type switch { WebSocketMessageType.Binary => "binary", _ => "text" }),
        };
        using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
        string workflow = "workflow";
        string fixedReply = "fixed";
        if (string.Equals(mode, workflow, StringComparison.Ordinal))
        {
            using ReplyRequestLease pending = replies.Register(message.Context.PeerId, "websocket",
                (value, token) => WebSocketMessages.SendAsync(socket, value, message.Context, token), deadline.Token);
            outputs["request"] = pending.Handle;
            await context.EmitAsync(message.Context, new(EOutcome.Succeeded, "success", outputs), cancellationToken).ConfigureAwait(false);
            NodeResult reply = await pending.Completion.ConfigureAwait(false);
            if (reply.Outcome != EOutcome.Succeeded)
            {
                socket.Abort();
            }
            return;
        }
        if (string.Equals(mode, fixedReply, StringComparison.Ordinal))
        {
            NodeValue response = NodeValue.FromJson(settings.Get("reply"));
            NodeResult sent = await WebSocketMessages.SendAsync(socket, response, message.Context, deadline.Token).ConfigureAwait(false);
            if (sent.Outcome != EOutcome.Succeeded)
            {
                await context.EmitAsync(message.Context, sent, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        await context.EmitAsync(message.Context, new(EOutcome.Succeeded, "success", outputs), cancellationToken).ConfigureAwait(false);
    }
}
