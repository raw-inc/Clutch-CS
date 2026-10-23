using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>管理APIと分離したKestrelでHTTP要求をワークフロー入力にします。</summary>
internal sealed class HttpListenNode : ISourceNode
{
    private readonly CommunicationReplyRegistry replies;

    /// <summary>ワークフロー返信と共有するregistryを受け取ります。</summary>
    internal HttpListenNode(CommunicationReplyRegistry replies)
    {
        this.replies = replies;
    }

    /// <summary>固定応答を持つHTTP受信設定です。</summary>
    public NodeDescriptor Descriptor => new("http.listen", "HTTP listener", "Communication", "REST/GraphQL envelopeを受信します。GraphQL schema実行engineは含みません。", ENodeKind.Source, EExecutionLocation.ExplicitPeer,
        [new("method", EPortValueKind.Json, OverridesSetting: "method"), new("queryType", EPortValueKind.Json, OverridesSetting: "queryType"), new("bodyType", EPortValueKind.Json, OverridesSetting: "bodyType")],
        [new("value", EPortValueKind.Json), new("request", EPortValueKind.Json), new("query", EPortValueKind.Json), new("body"), new("bodyContentType", EPortValueKind.Json)], ["success"], Values.StructuredValueTypes.Schema(HttpSettings.Schema(true), "query", "body"));

    /// <summary>待受と応答設定を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return [.. NodeSchemaValidator.Validate(definition, Descriptor), .. HttpSettings.Validate(definition, true), .. HttpInputValues.Validate(definition)];
    }

    /// <summary>各要求の受付時に設定を固定します。</summary>
    /// <returns>予定正常終了または取消等の結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        if (NodeSupport.Invalid(context.Initial, this) is { } invalid)
        {
            return invalid;
        }
        try
        {
            HttpInputValues.RequireTypes(context.Initial);
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return NodeResult.Failure("http.parameter.type", "Check the selected parameter type or JSON Schema.", Contracts.Execution.EOutcome.NotExecuted);
        }
        int received = 0;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        try
        {
            while (!lifetime.IsCancellationRequested && !context.IsStopRequested)
            {
                try
                {
                    NodeResult result = await RunBindingAsync(context, () => Interlocked.Increment(ref received), cancellationToken).ConfigureAwait(false);
                    string unavailable = "http.listen.unavailable";
                    if (!string.Equals(result.ReasonCode, unavailable, StringComparison.Ordinal))
                    {
                        return result;
                    }
                }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException)
                {
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, "network.binding.unresolved", "同一NICと指定IPの回復を待機します。"), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return CommunicationSupport.Terminal(cancellationToken);
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token).ConfigureAwait(false);
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

    /// <summary>一つのbinding寿命でHTTPサーバーを所有します。</summary>
    /// <returns>正常終了または再待受が必要な結果です。</returns>
    private async Task<NodeResult> RunBindingAsync(NodeSourceContext context, Func<int> incrementReceived, CancellationToken cancellationToken = default)
    {
        NodeSettings initial = new(context.Initial.Settings);
        LocalNetworkBinding binding = new(context.Initial.Settings, context.Initial.HostId);
        using NetworkBindingLease lease = new(binding, cancellationToken);
        using CancellationTokenSource completed = new();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, context.StopToken, completed.Token);
        RequestDelegate handler = async http =>
        {
            if (context.IsStopRequested)
            {
                http.Response.StatusCode = 503;
                return;
            }
            NodeExecutionContext item = context.BeginItem();
            NodeSettings settings = new(item.Settings);
            string path = settings.ReadText("path", "/") ?? string.Empty;
            if (!http.Request.Path.Equals(new PathString(path), StringComparison.Ordinal))
            {
                http.Response.StatusCode = 404;
                return;
            }
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, http.RequestAborted);
            using CancellationTokenSource requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            bool emitted = false;
            try
            {
                _ = binding.Resolve();
                string method = HttpInputValues.Method(item, true);
                string anyMethod = "*";
                if (!string.Equals(method, anyMethod, StringComparison.Ordinal) && !string.Equals(method, http.Request.Method, StringComparison.Ordinal))
                {
                    http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    http.Response.Headers.Allow = method;
                    return;
                }
                byte[] bytes = await HttpSettings.ReadBoundedAsync(http.Request.Body, settings.ReadInt32("maxBytes", 1048576), requestLifetime.Token).ConfigureAwait(false);
                await CommunicationTrace.WriteAsync(item, "http", false, bytes, CancellationToken.None).ConfigureAwait(false);
                (string bodyFormat, string effectiveMediaType) = Contracts.Values.HttpBodyFormats.Resolve(item.Settings, http.Request.ContentType);
                string binaryFormat = "binary";
                bool binary = string.Equals(bodyFormat, binaryFormat, StringComparison.Ordinal);
                string? body = null;
                string contentType = http.Request.ContentType ?? "application/octet-stream";
                if (!binary)
                {
                    body = new UTF8Encoding(false, true).GetString(bytes);
                }
                string mode = settings.ReadText("mode", "rest") ?? string.Empty;
                string graphQl = "graphql";
                if (string.Equals(mode, graphQl, StringComparison.Ordinal))
                {
                    using JsonDocument request = JsonDocument.Parse(body ?? throw new FormatException("GraphQL requires a JSON body."));
                    HttpSettings.ValidateGraphQlRequest(request.RootElement);
                }
                string replyMode = settings.ReadText("replyMode", "fixed") ?? string.Empty;
                string workflow = "workflow";
                NodeValue query = HttpInputValues.Query(item, http.Request.Query);
                NodeValue parsedBody = binary ? NodeValue.FromBytes(bytes)
                                              : HttpInputValues.Body(item, body!, bodyFormat);
                string legacy = "legacy";
                NodeValue requestInfo = string.Equals(bodyFormat, legacy, StringComparison.Ordinal) ? NodeValue.FromObject(new { method = http.Request.Method, path = http.Request.Path.Value, body })
                                                                                                    : NodeValue.FromObject(new { method = http.Request.Method, path = http.Request.Path.Value, body, contentType, bodyLength = bytes.Length });
                NodeResult result = NodeResult.Success() with
                {
                    Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                    {
                        ["value"] = requestInfo,
                        ["query"] = query,
                        ["body"] = parsedBody,
                        ["bodyContentType"] = NodeValue.FromObject(effectiveMediaType),
                    },
                };
                if (string.Equals(replyMode, workflow, StringComparison.Ordinal))
                {
                    HttpReplyWriter writer = new(http, settings.ReadInt32("maxBytes", 1048576), item);
                    using ReplyRequestLease reply = replies.Register(item.PeerId, "http", writer.WriteAsync, requestLifetime.Token);
                    Dictionary<string, NodeValue> outputs = new(result.Outputs, StringComparer.Ordinal)
                    {
                        ["request"] = reply.Handle,
                    };
                    emitted = true;
                    await context.EmitAsync(item, result with { Outputs = outputs }, cancellationToken).ConfigureAwait(false);
                    NodeResult response = await reply.Completion.ConfigureAwait(false);
                    if (response.Outcome != EOutcome.Succeeded && !http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
                    {
                        http.Response.StatusCode = 500;
                        string expired = "reply.expired";
                        if (string.Equals(response.ReasonCode, expired, StringComparison.Ordinal))
                        {
                            http.Response.StatusCode = 504;
                        }
                    }
                }
                else
                {
                    string responseBody = settings.ReadText("responseBody", "{}") ?? string.Empty;
                    if (string.Equals(mode, graphQl, StringComparison.Ordinal))
                    {
                        HttpSettings.ValidateGraphQlResponse(responseBody);
                    }
                    emitted = true;
                    await context.EmitAsync(item, result, cancellationToken).ConfigureAwait(false);
                    http.Response.StatusCode = settings.ReadInt32("responseStatus", 200);
                    http.Response.ContentType = settings.ReadText("contentType", "application/json");
                    await http.Response.WriteAsync(responseBody, requestLifetime.Token).ConfigureAwait(false);
                    await CommunicationTrace.WriteAsync(item, "http", true, System.Text.Encoding.UTF8.GetBytes(responseBody), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (ReplyCapacityException)
            {
                http.Response.StatusCode = 503;
                await context.EmitAsync(item, NodeResult.Failure("http.reply.capacity", "返信待ち上限のため要求を開始できません。", EOutcome.NotExecuted), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or FormatException or InvalidOperationException or OperationCanceledException or DecoderFallbackException or OverflowException)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = 400;
                }
                if (!emitted)
                {
                    await context.EmitAsync(item, NodeResult.Failure("http.request.invalid", "HTTP入力の取得・形式確認を完了できません。"), cancellationToken).ConfigureAwait(false);
                }
            }
            int count = settings.ReadInt32("count");
            int total = incrementReceived();
            if (0 < count && count <= total)
            {
                await completed.CancelAsync().ConfigureAwait(false);
            }
        };
        try
        {
            HttpEndpointSet endpoint = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable endpointLifetime = endpoint.ConfigureAwait(false);
            await endpoint.OpenAsync(binding, ListenPorts.Read(initial.Get("localPort")), initial.ReadText("path", "/")!, handler, lifetime.Token).ConfigureAwait(false);
            await context.ReportStatusAsync(new(ESourceState.Listening, null, endpoint.Urls), lifetime.Token).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (lease.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !context.IsStopRequested && !completed.IsCancellationRequested)
            {
                await context.ReportStatusAsync(new(ESourceState.Unavailable, "http.listen.unavailable"), cancellationToken).ConfigureAwait(false);
                return NodeResult.Failure("http.listen.unavailable", "指定NICとIPの回復を待機します。");
            }
            return CommunicationSupport.Terminal(cancellationToken);
        }
        catch (HttpEndpointConflictException exception)
        {
            await context.ReportStatusAsync(new(ESourceState.Failed, "http.endpoint.duplicate", exception.Message), cancellationToken).ConfigureAwait(false);
            return NodeResult.Failure("http.endpoint.duplicate", exception.Message, EOutcome.NotExecuted);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await context.ReportStatusAsync(new(ESourceState.Unavailable, "http.listen.unavailable"), cancellationToken).ConfigureAwait(false);
            return NodeResult.Failure("http.listen.unavailable", "HTTP待受を開始・維持できません。");
        }
        return CommunicationSupport.Terminal(cancellationToken);
    }
}
