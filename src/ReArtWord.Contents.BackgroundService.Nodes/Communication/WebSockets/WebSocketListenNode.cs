using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;

/// <summary>明示NICで複数のWebSocket接続を受け付ける継続入力です。</summary>
internal sealed class WebSocketListenNode(CommunicationReplyRegistry replies) : ISourceNode
{
    /// <summary>受信・件数・返信の設定です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = new("websocket.listen", "WebSocket listener", "Communication", "Receive complete text or binary messages and optionally reply from the workflow.", ENodeKind.Source, EExecutionLocation.ExplicitPeer,
        [], [new("value"), new("messageType", EPortValueKind.Json), new("request", EPortValueKind.Json)], ["success"], NodeSettings.Schema(WebSocketSettings.Schema(true)));

    /// <summary>固定返信の欠落も開始前に拒否します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor), .. WebSocketSettings.Validate(definition, true)];
        NodeSettings settings = new(definition.Settings);
        string mode = settings.ReadText("replyMode", "none")!;
        string fixedMode = "fixed";
        System.Text.Json.JsonElement response = settings.Get("reply");
        if (string.Equals(mode, fixedMode, StringComparison.Ordinal) && response.ValueKind == System.Text.Json.JsonValueKind.Undefined)
        {
            issues.Add(new("websocket.replyRequired", "固定返信を設定してください。", definition.Id, "reply"));
        }
        return issues;
    }

    /// <summary>正常停止では新受付を止め、取得済みのフレームと返信を維持します。</summary>
    /// <returns>待受の正常終了、取消、または利用不能です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (NodeSupport.Invalid(context.Initial, this) is { } invalid)
        {
            return invalid;
        }
        NodeSettings settings = new(context.Initial.Settings);
        using CancellationTokenSource completed = new();
        int limit = settings.ReadInt32("count");
        long admitted = 0, finished = 0;
        Func<bool> admit = () => limit == 0 || Interlocked.Increment(ref admitted) <= limit;
        Action complete = () =>
        {
            if (0 < limit && limit <= Interlocked.Increment(ref finished))
            {
                completed.Cancel();
            }
        };
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.StopToken, completed.Token, cancellationToken);
        try
        {
            while (!lifetime.IsCancellationRequested && !context.IsStopRequested)
            {
                try
                {
                    NodeResult result = await RunBindingAsync(context, admit, complete, completed.Token, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome != EOutcome.Failed)
                    {
                        return result;
                    }
                }
                catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
                {
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, "websocket.bindingUnavailable"), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return CommunicationSupport.Terminal(cancellationToken);
                }
                try
                {
                    await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return CommunicationSupport.Terminal(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 待受の復旧処理や状態通知の途中でも、停止要求に対応する終了結果を返します。
        }
        return CommunicationSupport.Terminal(cancellationToken);
    }

    /// <summary>同じNIC/IPの一回の待受を所有し、再開しても受信済み件数は維持します。</summary>
    /// <returns>待受の終了または同一bindingの復旧待ちです。</returns>
    private async Task<NodeResult> RunBindingAsync(NodeSourceContext context, Func<bool> admit, Action complete, CancellationToken completed,
        CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Initial.Settings);
        LocalNetworkBinding binding = new(context.Initial.Settings, context.Initial.HostId);
        using NetworkBindingLease lease = new(binding, cancellationToken);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.StopToken, completed);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, lease.Token);
        using SemaphoreSlim connections = new(settings.ReadInt32("maxConnections", 32));
        WebSocketConversation conversation = new(context, replies, admit, complete);
        PathString path = new(settings.ReadText("path", "/"));
        RequestDelegate handler = async http =>
        {
            if (stop.IsCancellationRequested || context.IsStopRequested)
            {
                http.Response.StatusCode = 503;
                return;
            }
            if (!http.Request.Path.Equals(path, StringComparison.Ordinal))
            {
                http.Response.StatusCode = 404;
                return;
            }
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = 400;
                return;
            }
            if (!await connections.WaitAsync(0, lease.Token).ConfigureAwait(false))
            {
                http.Response.StatusCode = 503;
                return;
            }
            try
            {
                using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                using CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, http.RequestAborted);
                await conversation.RunAsync(socket, stop.Token, connection.Token).ConfigureAwait(false);
            }
            finally
            {
                connections.Release();
            }
        };
        try
        {
            HttpEndpointSet endpoint = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable endpointLifetime = endpoint.ConfigureAwait(false);
            await endpoint.OpenAsync(binding, ListenPorts.Read(settings.Get("localPort")), path.Value!, handler, waiting.Token).ConfigureAwait(false);
            await context.ReportStatusAsync(new(ESourceState.Listening, Message: endpoint.Urls), cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await context.ReportStatusAsync(new(ESourceState.Unavailable, "websocket.bindingUnavailable"), cancellationToken).ConfigureAwait(false);
                return NodeResult.Failure("websocket.bindingUnavailable", "指定NICまたはIPを確認できません。");
            }
        }
        catch (HttpEndpointConflictException exception)
        {
            await context.ReportStatusAsync(new(ESourceState.Failed, "http.endpoint.duplicate", exception.Message), cancellationToken).ConfigureAwait(false);
            return NodeResult.Failure("http.endpoint.duplicate", exception.Message, EOutcome.NotExecuted);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await context.ReportStatusAsync(new(ESourceState.Unavailable, "websocket.listenUnavailable"), cancellationToken).ConfigureAwait(false);
            return NodeResult.Failure("websocket.listenUnavailable", "WebSocket待受を開始できません。");
        }
        return CommunicationSupport.Terminal(cancellationToken);
    }
}
