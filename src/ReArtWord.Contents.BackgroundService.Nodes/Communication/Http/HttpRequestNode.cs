using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTP RESTおよびGraphQL envelopeを明示NICから送信します。</summary>
internal sealed class HttpRequestNode : IActionNode
{
    private readonly ISecretStore secrets;

    /// <summary>秘密値の解決先を注入します。</summary>
    internal HttpRequestNode(ISecretStore secrets)
    {
        this.secrets = secrets;
    }

    /// <summary>HTTP成功条件と接続設定です。</summary>
    public NodeDescriptor Descriptor => new("http.request", "HTTP request", "Communication", "RESTまたはGraphQL要求を送信し、指定した成功条件を検証します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
        [new("value"), new("method", EPortValueKind.Json, OverridesSetting: "method")],
        [new("value"), new("body"), new("statusCode", EPortValueKind.Json) { DataType = new("integer") }, new("contentType", EPortValueKind.Json) { DataType = new("string") },
            new("fileName", EPortValueKind.Json) { DataType = new("string") }, new("headers", EPortValueKind.Json) { DataType = new("object") }], ["success"], HttpSettings.Schema(false)) { HasAsyncCompletion = true };

    /// <summary>要求URIと成功条件を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return [.. NodeSchemaValidator.Validate(definition, Descriptor), .. HttpSettings.Validate(definition, false)];
    }

    /// <summary>自動redirect・retryなしで要求と応答を処理します。</summary>
    /// <returns>本HTTP処理の結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return invalid;
        }
        bool dispatched = false;
        bool responseReceived = false;
        try
        {
            NodeSettings settings = new(context.Settings);
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
            using SocketsHttpHandler handler = HttpTransportFactory.Create(new LocalNetworkBinding(context.Settings, context.HostId), settings.ReadInt32("localPort"));
            using HttpClient client = new(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
            using HttpRequestMessage request = new(new HttpMethod(HttpInputValues.Method(context, false)), settings.ReadText("url"));
            string mode = settings.ReadText("mode", "rest") ?? string.Empty;
            string graphQl = "graphql";
            string valuePort = "value";
            if (context.Inputs.TryGetValue(valuePort, out NodeValue? input))
            {
                if (string.Equals(mode, graphQl, StringComparison.Ordinal))
                {
                    HttpSettings.ValidateGraphQlRequest(input.Json);
                }
                request.Content = new ByteArrayContent(CommunicationSupport.Encode(input, "raw"));
                request.Content.Headers.ContentType = new(settings.ReadText("contentType", "application/json") ?? string.Empty);
            }
            else
            {
                if (string.Equals(mode, graphQl, StringComparison.Ordinal))
                {
                    return NodeResult.Failure("http.graphql.query.required", "GraphQL query envelopeが必要です。", EOutcome.NotExecuted);
                }
            }
            string? secretId = settings.ReadText("secretId");
            string? secret = null;
            if (HttpAuthentication.Enabled(settings))
            {
                secret = await secrets.GetAsync(secretId!, deadline.Token).ConfigureAwait(false);
                if (secret is null)
                {
                    return NodeResult.Failure("http.secret.unavailable", "接続秘密値を利用できません。", EOutcome.NotExecuted);
                }
                string headerName = settings.ReadText("secretHeader", "Authorization") ?? string.Empty;
                string scheme = settings.ReadText("secretScheme", "Bearer") ?? string.Empty;
                string headerValue = secret;
                if (!string.IsNullOrEmpty(scheme))
                {
                    headerValue = string.Concat(scheme, " ", secret);
                }
                request.Headers.Add(headerName, headerValue);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (request.Content is not null)
            {
                byte[] outgoingBody = await request.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
                await CommunicationTrace.WriteAsync(context, "http", true, outgoingBody, CancellationToken.None).ConfigureAwait(false);
            }
            dispatched = true;
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            responseReceived = true;
            using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] bytes = await HttpSettings.ReadBoundedAsync(stream, settings.ReadInt32("maxBytes", 1048576), deadline.Token).ConfigureAwait(false);
            await CommunicationTrace.WriteAsync(context, "http", false, bytes, CancellationToken.None).ConfigureAwait(false);
            string body = Encoding.UTF8.GetString(bytes);
            string success = settings.ReadText("success", "status") ?? string.Empty;
            bool confirmed = HttpSettings.Confirm(response.StatusCode, body, settings, success);
            if (!confirmed)
            {
                return NodeResult.Failure("http.response.rejected", FormattableString.Invariant($"HTTP応答が指定成功条件を満たしません。受信ステータス: {(int)response.StatusCode}、期待ステータス: {settings.ReadInt32("expectedStatus", 200)}。"));
            }
            if (string.Equals(mode, graphQl, StringComparison.Ordinal))
            {
                HttpSettings.ValidateGraphQlResponse(body);
            }

            return HttpResponseValues.Create(response, bytes, body, settings.ReadText("responseBodyFormat", "legacy")!, secret);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or FormatException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "http.exception", exception, false, CancellationToken.None).ConfigureAwait(false);
            }
            return HttpRequestFailure.From(exception, dispatched, responseReceived, cancellationToken.IsCancellationRequested);
        }
    }
}
