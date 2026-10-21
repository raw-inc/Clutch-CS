using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>配列要素・HTTPパラメーターの型指定モードを同じ保存・検証規則で扱います。</summary>
internal static class StructuredValueTypes
{
    private static readonly string[] modes = ["auto", "schema", "manual"];
    private static readonly string[] schemaModes = ["schema"];
    private static readonly string[] manualModes = ["manual"];

    /// <summary>既存設定へ三つの型指定モードを追加します。</summary>
    /// <returns>共通の型エディターを指定したSchemaです。</returns>
    internal static JsonElement Schema(JsonElement original, params string[] prefixes)
    {
        JsonObject schema = JsonNode.Parse(original.GetRawText())!.AsObject();
        JsonObject properties = schema["properties"]!.AsObject();
        foreach (string prefix in prefixes)
        {
            string mode = prefix + "Mode";
            properties[mode] = JsonSerializer.SerializeToNode(new { type = "string", @enum = modes, @default = "auto" });
            properties[prefix + "Schema"] = JsonNode.Parse("""{"type":["object","boolean","string"],"x-multiline":true}""");
            properties[prefix + "Schema"]!["x-visibleWhen"] = JsonSerializer.SerializeToNode(new { property = mode, values = schemaModes, @default = "auto" });
            properties[prefix + "Type"] = JsonNode.Parse("""{"type":"object","x-type-definition":true}""");
            properties[prefix + "Type"]!["x-visibleWhen"] = JsonSerializer.SerializeToNode(new { property = mode, values = manualModes, @default = "auto" });
        }
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>既存の接続型入力を優先し、明示指定の欠損を自動扱いにしません。</summary>
    /// <returns>今回の開始時に固定する型、または自動のnullです。</returns>
    internal static ValueTypeDefinition? Read(JsonElement settings, IReadOnlyDictionary<string, NodeValue> inputs, string prefix)
    {
        string typePort = prefix + "Type";
        if (inputs.TryGetValue(typePort, out NodeValue? supplied))
        {
            return ValueTypeOperations.ReadType(supplied);
        }
        NodeSettings values = new(settings);
        string mode = values.ReadText(prefix + "Mode", "auto")!;
        ValueTypeDefinition? type = mode switch
        {
            "auto" => null,
            "manual" => ValueTypeOperations.ReadType(NodeValue.FromJson(values.Get(typePort))),
            "schema" => JsonSchemaTypes.Read(values.Get(prefix + "Schema")),
            _ => throw new FormatException("type.mode"),
        };
        if (type is not null)
        {
            ValueTypeOperations.ValidateType(type);
        }
        return type;
    }

    /// <summary>配線で後から供給される型は開始時に検証し、保存済み指定は編集時にも確認します。</summary>
    /// <returns>設定済み型定義の問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition node, string prefix)
    {
        try
        {
            NodeSettings settings = new(node.Settings);
            string? mode = settings.ReadText(prefix + "Mode", "auto");
            string manual = "manual";
            string typePort = prefix + "Type";
            if (string.Equals(mode, manual, StringComparison.Ordinal) && settings.Get(typePort).ValueKind == JsonValueKind.Undefined && node.Inputs?.ContainsKey(typePort) != true)
            {
                return [];
            }
            _ = Read(node.Settings, node.Inputs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal), prefix);
            return [];
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return [new("type.definition.invalid", "Check the selected manual type or JSON Schema definition.", node.Id, prefix + "Mode")];
        }
    }
}
