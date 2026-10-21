using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>明示した重複方針でオブジェクトへ一項目を追加します。</summary>
public sealed class KeyValueNode : PureValueNode
{
    /// <summary>キーは入力または設定から、値は必須入力から取得します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.keyValue", "Key and value", "Values",
        """{"type":"object","properties":{"key":{"type":"string","maxLength":4096},"duplicate":{"type":"string","enum":["reject","replace"]}}}""",
        inputs: [new("key", EPortValueKind.Json, OverridesSetting: "key"), new("value", EPortValueKind.Json, true), new("base", EPortValueKind.Json)], outputs: [new("value", EPortValueKind.Json)]) with { Description = "Add a JSON property. Duplicate keys are rejected unless replacement is explicitly selected." };

    /// <summary>既存の構造と型定義JSONをそのまま保持します。</summary>
    /// <returns>独立したJSONオブジェクトです。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string keyPort = "key";
        string valuePort = "value";
        string basePort = "base";
        NodeSettings settings = new(definition.Settings);
        string? key = settings.ReadText("key");
        if (inputs.TryGetValue(keyPort, out NodeValue? supplied))
        {
            if (supplied.Kind != EValueKind.Json || supplied.Json.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("key.string.required");
            }
            key = supplied.Json.GetString();
        }
        if (key is null || !inputs.TryGetValue(valuePort, out NodeValue? item))
        {
            throw new FormatException("keyValue.input.required");
        }
        ValueTypeOperations.ValidateUnicode(key);
        ValueTypeOperations.ValidateValue(item, new("any"));
        if (item.Kind != EValueKind.Json)
        {
            throw new FormatException("binary.nesting.unsupported");
        }
        JsonObject result = [];
        if (inputs.TryGetValue(basePort, out NodeValue? source))
        {
            ValueTypeOperations.ValidateValue(source, new("object"));
            result = JsonNode.Parse(source.Json.GetRawText())!.AsObject();
        }
        string replace = "replace";
        string duplicatePolicy = settings.ReadText("duplicate", "reject")!;
        if (result.ContainsKey(key) && !string.Equals(duplicatePolicy, replace, StringComparison.Ordinal))
        {
            throw new FormatException("key.duplicate");
        }
        result[key] = JsonNode.Parse(item.Json.GetRawText());
        NodeValue value = NodeValue.FromJson(JsonSerializer.SerializeToElement(result));
        ValueTypeOperations.ValidateValue(value, new("object"));
        return DictionaryValues.Build(PathTypeMetadata.WithProperty(value, source, key, item), null);
    }
}
