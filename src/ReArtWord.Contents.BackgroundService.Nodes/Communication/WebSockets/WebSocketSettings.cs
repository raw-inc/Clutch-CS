using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;

/// <summary>ワークフローWebSocketの接続設定と有限のメッセージ上限です。</summary>
internal static class WebSocketSettings
{
    /// <summary>役割ごとに必要な設定だけを公開します。</summary>
    /// <returns>既存エディターで編集可能なJSON Schemaです。</returns>
    internal static string Schema(bool server)
    {
        string required = "\"interfaceId\",\"localAddress\",\"url\"";
        string extra = "\"url\":{\"type\":\"string\",\"x-runtime-input\":true},\"secretId\":{\"type\":\"string\"},\"awaitReply\":{\"type\":\"boolean\",\"default\":true},";
        string localPort = "\"localPort\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":65535";
        if (!server)
        {
            localPort += ",\"x-runtime-input\":true";
        }
        if (server)
        {
            required = "\"interfaceId\",\"localAddress\"";
            extra = "\"path\":{\"type\":\"string\",\"default\":\"/\"},\"count\":{\"type\":\"integer\",\"minimum\":0},\"maxConnections\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":128,\"default\":32},\"replyMode\":{\"type\":\"string\",\"enum\":[\"none\",\"fixed\",\"workflow\"]},\"reply\":{},";
        }
        string schema = "{\"type\":\"object\",\"required\":[" + required + "],\"properties\":{" + extra
            + "\"interfaceId\":{\"type\":\"string\",\"minLength\":1},\"localAddress\":{\"type\":\"string\"}," + localPort
            + "},\"timeoutMs\":{\"type\":[\"integer\",\"null\"],\"minimum\":0,\"maximum\":2147483646,\"default\":30000},\"maxBytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16777216,\"default\":1048576}}}";
        System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(schema)!.AsObject();
        if (server)
        {
            root["properties"]!["localPort"] = System.Text.Json.Nodes.JsonNode.Parse(ListenPorts.Schema.GetRawText());
        }
        return NetworkBindingSettings.Schema(System.Text.Json.JsonSerializer.SerializeToElement(root)).GetRawText();
    }

    /// <summary>NIC指定と接続先の意味を確認します。</summary>
    /// <returns>接続開始前の問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition, bool server)
    {
        List<ValidationIssue> issues = [.. CommunicationSupport.Validate(definition, false)];
        NodeSettings settings = new(definition.Settings);
        if (server)
        {
            string path = settings.ReadText("path", "/")!;
            if (!path.StartsWith('/') || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
            {
                issues.Add(new("websocket.path", "待受パスは/で始まるパスを指定してください。", definition.Id));
            }
        }
        else
        {
            string? address = settings.ReadText("url");
            string ws = "ws", wss = "wss";
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || (!string.Equals(uri.Scheme, ws, StringComparison.Ordinal) && !string.Equals(uri.Scheme, wss, StringComparison.Ordinal)) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            {
                issues.Add(new("websocket.url", "資格情報やfragmentを含まないws/wss URLが必要です。", definition.Id, "/url"));
            }
        }
        return issues;
    }
}
