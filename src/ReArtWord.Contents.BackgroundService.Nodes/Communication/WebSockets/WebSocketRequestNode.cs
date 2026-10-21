using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;

/// <summary>指定NICで一接続・任意送信・任意一返信を行うWebSocketクライアントです。</summary>
internal sealed class WebSocketRequestNode(ISecretStore secrets) : IActionNode
{
    /// <summary>接続単位の期限・送信値と返信確認方針です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = new("websocket.request", "WebSocket request", "Communication", "Open one WebSocket connection, optionally send a message, and optionally receive one complete message.",
        ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new("value")], [new("value"), new("messageType", EPortValueKind.Json)], ["success"], NodeSettings.Schema(WebSocketSettings.Schema(false))) { HasAsyncCompletion = true };

    /// <summary>接続設定を開始前に確認します。</summary>
    /// <returns>構成の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
        => [.. NodeSupport.Validate(definition, Descriptor), .. WebSocketSettings.Validate(definition, false)];

    /// <summary>接続・送信を暗黙に再試行せず、一接続の設定と秘密値を固定します。</summary>
    /// <returns>実取得した返信、または送信後不明等の結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return invalid;
        }
        bool dispatched = false;
        bool connected = false;
        using ClientWebSocket socket = new();
        try
        {
            NodeSettings settings = new(context.Settings);
            NodeValue? input = NodeSupport.Input(context);
            int maximum = settings.ReadInt32("maxBytes", 1048576);
            if (input is not null && maximum < WebSocketMessages.Encode(input).Length)
            {
                return NodeResult.Failure("websocket.messageLimit", "送信メッセージが上限を超えています。", EOutcome.NotExecuted);
            }
            LocalNetworkBinding binding = new(context.Settings, context.HostId);
            using NetworkBindingLease lease = new(binding, cancellationToken);
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, lease.Token);
            string? name = settings.ReadText("secretId");
            if (!string.IsNullOrEmpty(name))
            {
                string? secret = await secrets.GetAsync(name, deadline.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(secret))
                {
                    return NodeResult.Failure("websocket.secretUnavailable", "接続秘密値を確認できません。", EOutcome.NotExecuted);
                }
                socket.Options.SetRequestHeader("Authorization", "Bearer " + secret);
            }
            using SocketsHttpHandler handler = HttpTransportFactory.Create(binding, settings.ReadInt32("localPort"));
            using HttpMessageInvoker transport = new(handler, false);
            await socket.ConnectAsync(new Uri(settings.ReadText("url")!), transport, deadline.Token).ConfigureAwait(false);
            connected = true;
            bool awaitReply = settings.ReadBoolean("awaitReply", true);
            if (input is not null)
            {
                NodeResult sent = await WebSocketMessages.SendAsync(socket, input, context, deadline.Token).ConfigureAwait(false);
                if (sent.Outcome != EOutcome.Succeeded)
                {
                    return sent;
                }
                dispatched = true;
                if (!awaitReply)
                {
                    return sent;
                }
            }
            if (!awaitReply)
            {
                return NodeResult.Success(NodeValue.FromObject(new { confirmation = "connected", bytesSent = 0 }));
            }
            WebSocketMessage? message = await WebSocketMessages.ReadAsync(socket, () => context, CancellationToken.None, deadline.Token).ConfigureAwait(false);
            if (message is null)
            {
                return NodeResult.Failure("websocket.replyMissing", "返信前に接続が終了しました。", EOutcome.Unknown);
            }
            return new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["value"] = message.Value,
                ["messageType"] = NodeValue.FromObject(message.Type switch { WebSocketMessageType.Binary => "binary", _ => "text" }),
            });
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (dispatched || connected)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("websocket.unconfirmed", "WebSocket処理の完了を確認できません。", outcome);
        }
        finally
        {
            await WebSocketMessages.CloseAsync(socket, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
