using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>通信ノードで共有する、共通指定とPC別指定の設定契約です。</summary>
public static class NetworkBindingSettings
{
    /// <summary>保存するネットワーク指定方式の設定名です。</summary>
    public static string ModeKey => "networkBindingMode";

    /// <summary>PCごとのNICとIPv4を保存する設定名です。</summary>
    public static string BindingsKey => "networkBindings";

    /// <summary>既存のNIC・IPv4設定を使う方式です。</summary>
    public static string ExplicitMode => "explicit";

    /// <summary>実行PCに一致する割り当てだけを使う方式です。</summary>
    public static string PerHostMode => "perHost";

    /// <summary>PC別の割り当てを明示的に使用しているか確認します。</summary>
    /// <returns>PC別指定が選択されている場合はtrueです。</returns>
    public static bool IsPerHost(JsonElement settings) => settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(ModeKey, out JsonElement mode)
        && mode.ValueKind == JsonValueKind.String && string.Equals(mode.GetString(), PerHostMode, StringComparison.Ordinal);

    /// <summary>この割り当て契約を宣言したノードか確認し、拡張ノードの同名設定と区別します。</summary>
    /// <returns>PC別ネットワーク設定を明示的に公開している場合はtrueです。</returns>
    public static bool Supports(JsonElement schema)
    {
        string propertiesKey = "properties", markerKey = "x-network-bindings";
        return schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(propertiesKey, out JsonElement properties)
            && properties.TryGetProperty(BindingsKey, out JsonElement bindings) && bindings.TryGetProperty(markerKey, out JsonElement marker) && marker.ValueKind == JsonValueKind.True;
    }

    /// <summary>既存の通信スキーマへ、共通指定と排他的なPC別指定を追加します。</summary>
    /// <returns>同じ規則を編集と実行時検証に使うスキーマです。</returns>
    public static JsonElement Schema(JsonElement schema)
    {
        JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        JsonObject properties = root["properties"]!.AsObject();
        properties.Insert(0, ModeKey, JsonNode.Parse("""{"type":"string","enum":["explicit","perHost"],"default":"explicit"}"""));
        foreach (string key in new[] { "interfaceId", "localAddress" })
        {
            JsonArray required = root["required"]!.AsArray();
            required.Remove(required.FirstOrDefault(item => string.Equals(item?.GetValue<string>(), key, StringComparison.Ordinal)));
            properties[key]!["x-visibleWhen"] = JsonNode.Parse("""{"property":"networkBindingMode","values":["explicit"],"default":"explicit"}""");
            properties[key]!["x-requiredWhen"] = properties[key]!["x-visibleWhen"]!.DeepClone();
        }
        properties.Insert(1, BindingsKey, JsonNode.Parse("""
            {"type":"array","minItems":1,"maxItems":256,"x-network-bindings":true,
             "x-visibleWhen":{"property":"networkBindingMode","values":["perHost"],"default":"explicit"},
             "x-requiredWhen":{"property":"networkBindingMode","values":["perHost"],"default":"explicit"},
             "items":{"type":"object","required":["hostId","interfaceId","localAddress"],"properties":{
                 "hostId":{"type":"string","minLength":1},"interfaceId":{"type":"string","minLength":1},"localAddress":{"type":"string","minLength":1}}}}
            """));
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>PC識別子の重複と、選択方式で使う明示NIC・IPv4を検証します。</summary>
    /// <returns>保存・開始前に訂正する割り当ての問題です。</returns>
    public static IReadOnlyList<ValidationIssue> Validate(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        List<ValidationIssue> issues = [];
        if (!IsPerHost(node.Settings))
        {
            ValidatePair(node.Settings, node.Id, string.Empty, issues);
            return issues;
        }
        if (!node.Settings.TryGetProperty(BindingsKey, out JsonElement bindings) || bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() == 0)
        {
            return [new("network.bindings.required", "Configure a network adapter and local IPv4 for each execution PC.", node.Id, "/" + BindingsKey)];
        }
        HashSet<string> hosts = new(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement binding in bindings.EnumerateArray())
        {
            string path = "/" + BindingsKey + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string host = Text(binding, "hostId");
            if (string.IsNullOrWhiteSpace(host) || !hosts.Add(host))
            {
                issues.Add(new("network.bindings.host", "Each network assignment needs a distinct, non-empty execution PC ID.", node.Id, path + "/hostId"));
            }
            ValidatePair(binding, node.Id, path, issues);
            index++;
        }
        return issues;
    }

    /// <summary>実行PCの一意な割り当てを取得し、未設定時は共通指定へ戻しません。</summary>
    /// <returns>開始する一回で固定するNICとIPv4です。</returns>
    public static HostNetworkBinding Select(JsonElement settings, string? hostId)
    {
        if (!IsPerHost(settings))
        {
            return new(hostId ?? string.Empty, Text(settings, "interfaceId"), Text(settings, "localAddress"));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        if (!settings.TryGetProperty(BindingsKey, out JsonElement bindings) || bindings.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("The execution PC's network assignment is missing.");
        }
        JsonElement[] matches = bindings.EnumerateArray().Where(item => string.Equals(Text(item, "hostId"), hostId, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException("The execution PC needs exactly one network assignment.");
        }
        return new(hostId, Text(matches[0], "interfaceId"), Text(matches[0], "localAddress"));
    }

    /// <summary>未知のJSON型を空値として検出し、編集途中にも例外を出しません。</summary>
    /// <returns>文字列値または未指定を表す空文字です。</returns>
    private static string Text(JsonElement value, string key)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.String)
        {
            return item.GetString()!;
        }
        return string.Empty;
    }

    /// <summary>全NIC待受へ広げず、明示されたIPv4とNICだけを許可します。</summary>
    private static void ValidatePair(JsonElement value, string nodeId, string path, List<ValidationIssue> issues)
    {
        string interfaceKey = "interfaceId", addressKey = "localAddress";
        if (string.IsNullOrWhiteSpace(Text(value, interfaceKey)))
        {
            issues.Add(new("network.interface.required", "interfaceIdが必要です。", nodeId, path + "/interfaceId"));
        }
        if (!IPAddress.TryParse(Text(value, addressKey), out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
        {
            issues.Add(new("network.localAddress.invalid", "明示IPv4 localAddressが必要です。", nodeId, path + "/localAddress"));
        }
    }
}
