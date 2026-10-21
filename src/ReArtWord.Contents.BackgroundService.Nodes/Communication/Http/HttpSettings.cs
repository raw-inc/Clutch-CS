using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTP envelope・設定・受信サイズの共通検証です。</summary>
internal static class HttpSettings
{
    /// <summary>HTTP設定のスキーマを作成します。</summary>
    /// <returns>対応範囲を明示するスキーマです。</returns>
    internal static JsonElement Schema(bool server)
    {
        string required = "[\"interfaceId\",\"localAddress\",\"url\"]";
        if (server)
        {
            required = "[\"interfaceId\",\"localAddress\",\"localPort\"]";
        }
        JsonElement schema = NodeSettings.Schema("{\"type\":\"object\",\"required\":" + required + """
            ,"properties":{"interfaceId":{"type":"string"},"localAddress":{"type":"string"},"localPort":{"type":"integer","minimum":0,"maximum":65535},"url":{"type":"string","x-runtime-input":true},"method":{"type":"string","enum":["GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"],"default":"POST"},"mode":{"type":"string","enum":["rest","graphql"],"default":"rest"},"path":{"type":"string","default":"/"},"contentType":{"type":"string","default":"application/json"},"secretId":{"type":"string"},"secretHeader":{"type":"string","default":"Authorization","minLength":1},"secretScheme":{"type":"string","default":"Bearer"},"timeoutMs":{"type":"integer","minimum":0,"maximum":2147483647,"default":30000},"maxBytes":{"type":"integer","minimum":1,"maximum":16777216,"default":1048576},"success":{"type":"string","enum":["response","status","body"],"default":"status"},"expectedStatus":{"type":"integer","minimum":100,"maximum":599,"default":200},"expectedBody":{"type":"string"},"replyMode":{"type":"string","enum":["fixed","workflow"],"default":"fixed"},"responseStatus":{"type":"integer","minimum":200,"maximum":599,"default":200},"responseBody":{"type":"string","default":"{}"},"count":{"type":"integer","minimum":0,"default":0}}}
            """);
        System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
        System.Text.Json.Nodes.JsonObject properties = root["properties"]!.AsObject();
        if (!server)
        {
            properties["responseBodyFormat"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["legacy","auto","json","text","binary"],"default":"legacy","x-runtime-input":true,"description":"Response body format. Binary preserves original bytes for saving images or files. Automatic uses Content-Type and keeps unknown types as Binary. JSON parses the body. Legacy preserves existing response objects. Status, headers, Content-Type and the suggested file name are separate outputs; the destination path must be chosen explicitly."}""");
            properties["localPort"]!["x-runtime-input"] = true;
            properties["authentication"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["auto","none","secret"],"default":"auto","x-runtime-input":false,"description":"None sends no authentication header and does not read saved secrets. Automatic preserves existing settings: authenticate only when a saved key name is supplied. Saved secret requires a key name; use Authorization/Bearer or your API's header name and an empty prefix."}""");
            foreach (string key in new[] { "secretId", "secretHeader", "secretScheme" })
            {
                properties[key]!["x-visibleWhen"] = System.Text.Json.Nodes.JsonNode.Parse("""{"property":"authentication","values":["auto","secret"],"default":"auto"}""");
            }
            properties["secretId"]!["x-requiredWhen"] = System.Text.Json.Nodes.JsonNode.Parse("""{"property":"authentication","values":["secret"],"default":"auto"}""");
            properties["secretHeader"]!.AsObject().Remove("minLength");
        }
        properties["timeoutMs"]!["type"] = new System.Text.Json.Nodes.JsonArray("integer", "null");
        properties["timeoutMs"]!["description"] = "Per-item communication deadline in milliseconds. 0 or null disables it. This does not limit how long a source listens; use Stop listening after in Execution for that.";
        string[] unused = ["path", "responseStatus", "responseBody", "replyMode", "count"];
        if (server)
        {
            unused = ["url", "method", "secretId", "secretHeader", "secretScheme", "success", "expectedStatus", "expectedBody"];
        }
        foreach (string name in unused)
        {
            properties.Remove(name);
        }
        if (server)
        {
            properties["localPort"] = System.Text.Json.Nodes.JsonNode.Parse(ListenPorts.Schema.GetRawText());
            properties["method"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["*","GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"],"default":"*"}""");
            properties["bodyFormat"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["legacy","auto","json","text","binary"],"default":"legacy","description":"JSON validates the received body itself. Text and Binary ignore JSON type settings. Automatic uses Content-Type when recognized, otherwise the fallback. Explicit formats take precedence. Binary preserves bytes. Legacy preserves existing workflows. This does not change the response Content-Type."}""");
            properties["bodyFallback"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["json","text","binary"],"default":"binary","x-visibleWhen":{"property":"bodyFormat","values":["auto"],"default":"legacy"}}""");
            properties["bodyMediaType"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","default":"application/octet-stream","minLength":1,"x-visibleWhen":{"property":"bodyFormat","values":["binary","auto"],"default":"legacy"},"description":"Explicit or fallback binary Content-Type, for example image/png, audio/wav or application/octet-stream. Automatic inference takes precedence when the header is recognized. No internal file format conversion is performed."}""");
        }
        return NetworkBindingSettings.Schema(JsonSerializer.SerializeToElement(root));
    }

    /// <summary>HTTP固有設定と明示NICを検証します。</summary>
    /// <returns>開始前に訂正する問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition, bool server)
    {
        List<ValidationIssue> issues = [.. CommunicationSupport.Validate(definition, false)];
        NodeSettings settings = new(definition.Settings);
        if (!server)
        {
            issues.AddRange(HttpAuthentication.Validate(definition));
            string? url = settings.ReadText("url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new("http.url.invalid", "HTTPまたはHTTPSの絶対URIが必要です。", definition.Id, "/url"));
            }
        }
        string success = settings.ReadText("success", "status") ?? string.Empty;
        string bodyMode = "body";
        JsonElement expectedBody = settings.Get("expectedBody");
        if (string.Equals(success, bodyMode, StringComparison.Ordinal) && expectedBody.ValueKind != JsonValueKind.String)
        {
            issues.Add(new("http.success.body.required", "body確認にはexpectedBodyが必要です。", definition.Id));
        }
        string? mode = settings.ReadText("mode");
        string graphql = "graphql";
        string? replyMode = settings.ReadText("replyMode", "fixed");
        string fixedMode = "fixed";
        if (server && string.Equals(replyMode, fixedMode, StringComparison.Ordinal) && string.Equals(mode, graphql, StringComparison.Ordinal))
        {
            try
            {
                ValidateGraphQlResponse(settings.ReadText("responseBody", "{}") ?? string.Empty);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                issues.Add(new("http.graphql.response.invalid", "GraphQL応答にはdataを含みerrorsのないJSONが必要です。", definition.Id));
            }
        }
        return issues;
    }

    /// <summary>有限サイズ内だけを読み込みます。</summary>
    /// <returns>読み取ったバイト列です。</returns>
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 16777216)
        {
            throw new FormatException("HTTPデータ上限が不正です。");
        }
        using MemoryStream output = new();
        byte[] buffer = new byte[Math.Min(maximum + 1, 8192)];
        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        while (read != 0)
        {
            if (maximum < output.Length + read)
            {
                throw new FormatException("HTTPデータ上限を超えました。");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    /// <summary>HTTP成功確認の段階を評価します。</summary>
    /// <returns>要求した確認が得られたかです。</returns>
    internal static bool Confirm(HttpStatusCode status, string body, NodeSettings settings, string success)
    {
        switch (success)
        {
            case "response":
                return true;
            case "status":
                return (int)status == settings.ReadInt32("expectedStatus", 200);
            case "body":
                return (int)status == settings.ReadInt32("expectedStatus", 200) && string.Equals(body, settings.ReadText("expectedBody"), StringComparison.Ordinal);
            default:
                throw new FormatException("HTTP成功確認が不正です。");
        }
    }

    /// <summary>GraphQLのHTTP 照会 envelopeを検証します。</summary>
    internal static void ValidateGraphQlRequest(JsonElement input)
    {
        string queryName = "query";
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(queryName, out JsonElement query) || query.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(query.GetString()))
        {
            throw new FormatException("GraphQL queryが必要です。");
        }
        string variablesName = "variables";
        if (input.TryGetProperty(variablesName, out JsonElement variables) && variables.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
        {
            throw new FormatException("GraphQL variablesはobjectまたはnullが必要です。");
        }
        string operationName = "operationName";
        if (input.TryGetProperty(operationName, out JsonElement operation) && operation.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            throw new FormatException("GraphQL operationNameはstringまたはnullが必要です。");
        }
    }

    /// <summary>GraphQLの最終応答envelopeとerrorsを検証します。</summary>
    internal static void ValidateGraphQlResponse(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string errorsName = "errors";
        string dataName = "data";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(dataName, out _))
        {
            throw new FormatException("GraphQL dataがありません。");
        }
        if (root.TryGetProperty(errorsName, out JsonElement errors) && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
        {
            throw new FormatException("GraphQL errors応答です。");
        }
    }
}
