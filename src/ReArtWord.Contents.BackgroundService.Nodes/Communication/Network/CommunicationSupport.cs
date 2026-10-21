using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.ArtNet;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;

/// <summary>通信ノードの共通設定・値変換を提供します。</summary>
internal static class CommunicationSupport
{
    /// <summary>一件の明示待機上限を作成します。</summary>
    /// <returns>呼出側が破棄する期限トークンです。</returns>
    internal static CancellationTokenSource Deadline(NodeSettings settings, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int milliseconds = 0;
        string timeout = "timeoutMs";
        if (settings.Get(timeout).ValueKind != JsonValueKind.Null)
        {
            milliseconds = settings.ReadInt32("timeoutMs", 30000);
        }
        if (0 < milliseconds)
        {
            source.CancelAfter(milliseconds);
        }
        return source;
    }

    /// <summary>既知接続線形式のdecode結果だけを型として宣言します。</summary>
    /// <returns>未登録形式は未確定のAnyです。</returns>
    internal static EPortValueKind DecodedKind(string format) => format switch
    {
        "raw" => EPortValueKind.Binary,
        "osc" or "artnet" => EPortValueKind.Json,
        _ => EPortValueKind.Any,
    };

    /// <summary>パケット構造を必要とする送信と、文字列・バイト列を送る汎用通信を区別します。</summary>
    /// <returns>送信処理が受け付ける入力形式です。</returns>
    internal static EPortValueKind EncodedKind(string format) => format switch
    {
        "osc" or "artnet" => EPortValueKind.Json,
        _ => EPortValueKind.Any,
    };

    /// <summary>通信設定の型と値を開始前に検証します。</summary>
    /// <returns>設定不足または不正値です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition, bool destination)
    {
        List<ValidationIssue> issues = [.. NetworkBindingSettings.Validate(definition)];
        NodeSettings settings = new(definition.Settings);

        if (!destination)
        {
            try
            {
                _ = ListenPorts.Read(settings.Get("localPort"));
            }
            catch (FormatException exception)
            {
                issues.Add(new("network.localPort.invalid", exception.Message, definition.Id, "/localPort"));
            }
        }

        if (destination)
        {

            string? remoteAddress = settings.ReadText("remoteAddress");
            if (!IPAddress.TryParse(remoteAddress, out IPAddress? remote) || remote.AddressFamily != AddressFamily.InterNetwork)
            {
                issues.Add(new("network.remoteAddress.invalid", "明示IPv4 remoteAddressが必要です。", definition.Id, "/remoteAddress"));
            }
            JsonElement remotePort = settings.Get("remotePort");
            if (remotePort.ValueKind == JsonValueKind.Undefined)
            {
                issues.Add(new("network.remotePort.required", "remotePortが必要です。", definition.Id, "/remotePort"));
            }
        }
        return issues;
    }

    /// <summary>値を接続線表現へ変換します。</summary>
    /// <returns>送信するバイト列です。</returns>
    internal static byte[] Encode(NodeValue value, string format)
    {
        switch (format)
        {
            case "osc":
                return OscPacketCodec.Encode(value.Json);
            case "artnet":
                return ArtNetDmxCodec.Encode(value.Json);
            case "raw":
                if (value.Kind == EValueKind.Binary)
                {
                    return value.Binary.ToArray();
                }
                return Encoding.UTF8.GetBytes(value.GetString() ?? value.Json.GetRawText());
            default:
                throw new FormatException("通信形式が不正です。");
        }
    }

    /// <summary>接続線表現を型付き値へ変換します。</summary>
    /// <returns>正常な一件の値です。</returns>
    internal static NodeValue Decode(byte[] bytes, string format)
    {
        switch (format)
        {
            case "osc":
                return NodeValue.FromJson(OscPacketCodec.Decode(bytes));
            case "artnet":
                return NodeValue.FromJson(ArtNetDmxCodec.Decode(bytes));
            default:
                return NodeValue.FromBytes(bytes);
        }
    }

    /// <summary>共通ソケット設定スキーマを作成します。</summary>
    /// <returns>編集と検証に公開するスキーマです。</returns>
    internal static JsonElement Schema(bool destination, string transport)
    {
        string required = "[\"interfaceId\",\"localAddress\"]";
        if (destination)
        {
            required = "[\"interfaceId\",\"localAddress\",\"remoteAddress\",\"remotePort\"]";
        }
        JsonElement schema = NodeSettings.Schema("{\"type\":\"object\",\"required\":" + required + """
            ,"properties":{"interfaceId":{"type":"string"},"localAddress":{"type":"string"},"localPort":{"default":0,"type":"integer","minimum":0,"maximum":65535},"remoteAddress":{"type":"string","x-runtime-input":true},"remotePort":{"type":"integer","x-runtime-input":true,"minimum":1,"maximum":65535},"broadcast":{"type":"boolean"},"timeoutMs":{"type":"integer","minimum":0,"maximum":2147483647,"default":30000,"description":"0は明示無期限"},"count":{"type":"integer","minimum":0,"default":0},"maxBytes":{"type":"integer","minimum":1,"maximum":16777216,"default":65507},"framing":{"type":"string","enum":["lengthPrefix","line","raw"],"default":"lengthPrefix"},"replyMode":{"type":"string","enum":["fixed","workflow"],"default":"fixed"},"awaitReply":{"type":"boolean","default":false},"reply":{"description":"受信後に返す固定値です。ワークフローから返信する場合、この値は使いません。","x-visibleWhen":{"property":"replyMode","values":["fixed"],"default":"fixed"}}}}
            """);
        System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
        System.Text.Json.Nodes.JsonObject properties = root["properties"]!.AsObject();
        properties["timeoutMs"]!["type"] = new System.Text.Json.Nodes.JsonArray("integer", "null");
        properties["timeoutMs"]!["description"] = "Per-item communication deadline in milliseconds. 0 or null disables it. This does not limit how long a source listens; use Stop listening after in Execution for that.";
        string udp = "udp";
        if (string.Equals(transport, udp, StringComparison.Ordinal))
        {
            properties.Remove("framing");
            properties.Remove("awaitReply");
            properties.Remove("reply");
            properties.Remove("replyMode");
            properties["maxBytes"]!["maximum"] = 65507;
        }
        else
        {
            properties.Remove("broadcast");
        }
        if (destination)
        {
            properties["localPort"]!["x-runtime-input"] = true;
            properties.Remove("replyMode");
            properties.Remove("count");
            properties.Remove("reply");
        }
        else
        {
            properties["localPort"] = System.Text.Json.Nodes.JsonNode.Parse(ListenPorts.Schema.GetRawText());
            properties.Remove("remoteAddress");
            properties.Remove("remotePort");
            properties.Remove("awaitReply");
            if (string.Equals(transport, udp, StringComparison.Ordinal))
            {
                properties["timeoutMs"]!["x-legacy"] = true;
                properties["broadcast"]!["x-legacy"] = true;
            }
        }
        return NetworkBindingSettings.Schema(JsonSerializer.SerializeToElement(root));
    }

    /// <summary>終了要求と緊急取消を区別します。</summary>
    /// <returns>待受の終了結果です。</returns>
    internal static NodeResult Terminal(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("network.cancelled", "待受が取り消されました。", EOutcome.Cancelled);
        }
        return NodeResult.Success();
    }
}
