using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>ワークフロー応答を検証してHTTP応答へ一度書き込みます。</summary>
internal sealed class HttpReplyWriter
{
    private readonly HttpContext context;
    private readonly int maximum;
    private readonly NodeExecutionContext traceContext;

    /// <summary>一つのHTTP受付と応答上限を固定します。</summary>
    internal HttpReplyWriter(HttpContext context, int maximum, NodeExecutionContext traceContext)
    {
        this.context = context;
        this.maximum = maximum;
        this.traceContext = traceContext;
    }

    /// <summary>送信開始前の不正と送信後不明を区別します。</summary>
    /// <returns>ローカルHTTP書込みの確認結果です。</returns>
    internal async Task<NodeResult> WriteAsync(NodeValue value, CancellationToken cancellationToken = default)
    {
        bool attempted = false;
        try
        {
            (int Status, string ContentType, byte[] Body) response = Prepare(value);
            if (response.Status is < 200 or > 599 || maximum < response.Body.Length || context.Response.HasStarted)
            {
                return NodeResult.Failure("http.reply.invalid", "返信status・sizeまたはHTTP状態が不正です。", EOutcome.NotExecuted);
            }
            cancellationToken.ThrowIfCancellationRequested();
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength = response.Body.Length;
            attempted = true;
            await CommunicationTrace.WriteAsync(traceContext, "http", true, response.Body, CancellationToken.None).ConfigureAwait(false);
            await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(new { statusCode = response.Status, bytesSent = response.Body.Length, confirmation = "localWrite" }));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or FormatException or JsonException or KeyNotFoundException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (attempted)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("http.reply.unconfirmed", "HTTP返信の完了を確認できません。", outcome);
        }
    }

    /// <summary>バイナリまたは明示HTTP envelopeから応答を組み立てます。</summary>
    /// <returns>書込み前に固定する応答です。</returns>
    private static (int Status, string ContentType, byte[] Body) Prepare(NodeValue value)
    {
        if (value.Kind == EValueKind.Binary)
        {
            return (200, "application/octet-stream", value.Binary.ToArray());
        }
        JsonElement json = value.Json;
        if (json.ValueKind != JsonValueKind.Object)
        {
            return (200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(value.GetString() ?? json.GetRawText()));
        }
        int status = 200;
        string contentType = "application/json";
        string statusName = "statusCode";
        string contentTypeName = "contentType";
        string bodyName = "body";
        string binaryName = "bodyBase64";
        if (json.TryGetProperty(statusName, out JsonElement statusValue))
        {
            status = statusValue.GetInt32();
        }
        if (json.TryGetProperty(contentTypeName, out JsonElement typeValue))
        {
            contentType = typeValue.GetString() ?? throw new FormatException("contentTypeが不正です。");
        }
        byte[] bytes;
        if (json.TryGetProperty(binaryName, out JsonElement binary))
        {
            bytes = binary.GetBytesFromBase64();
        }
        else
        {
            JsonElement body = json.GetProperty(bodyName);
            string text = body.GetRawText();
            if (body.ValueKind == JsonValueKind.String)
            {
                text = body.GetString() ?? string.Empty;
            }
            bytes = Encoding.UTF8.GetBytes(text);
        }
        return (status, contentType, bytes);
    }
}
