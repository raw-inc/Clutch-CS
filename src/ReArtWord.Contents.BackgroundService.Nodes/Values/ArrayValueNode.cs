using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>既存配列へ任意のJSON値または型定義を追加します。</summary>
public sealed class ArrayValueNode : PureValueNode
{
    /// <summary>空配列・既存配列・一件の追加を同じ契約で扱います。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.array", "Build array", "Values", """{"type":"object","properties":{}}""",
        inputs: [new("items", EPortValueKind.Json), new("item", EPortValueKind.Json), new("elementType", EPortValueKind.Json, OverridesSetting: "elementType")], outputs: [new("value", EPortValueKind.Json)]) with { SettingsSchema = StructuredValueTypes.Schema(JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), "element"), Description = "Append one JSON item to an array, or create an empty array. An optional element type validates every item." };

    /// <summary>元配列を変更せず、指定要素型を全件へ検証します。</summary>
    /// <returns>入れ子を保持したJSON配列です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string itemsPort = "items";
        string itemPort = "item";
        JsonArray array = [];
        if (inputs.TryGetValue(itemsPort, out NodeValue? items))
        {
            ValueTypeOperations.ValidateValue(items, new("array"));
            array = JsonNode.Parse(items.Json.GetRawText())!.AsArray();
        }
        if (inputs.TryGetValue(itemPort, out NodeValue? item))
        {
            ValueTypeOperations.ValidateValue(item, new("any"));
            if (item.Kind != EValueKind.Json)
            {
                throw new FormatException("binary.nesting.unsupported");
            }
            array.Add(JsonNode.Parse(item.Json.GetRawText()));
        }
        NodeValue value = NodeValue.FromJson(JsonSerializer.SerializeToElement(array));
        ValueTypeDefinition? element = StructuredValueTypes.Read(definition.Settings, inputs, "element");
        ValueTypeOperations.ValidateValue(value, new("array", element));
        List<ValueTypeDefinition?> candidates = [];
        if (items is not null)
        {
            candidates.AddRange(Enumerable.Range(0, items.Json.GetArrayLength()).Select(index => ValueTypeInference.Select(items.DataType, "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        if (item is not null)
        {
            candidates.Add(item.DataType);
        }
        if (array.Count == 0)
        {
            element ??= items?.DataType?.ElementType;
        }
        return PathTypeMetadata.Apply(value, PathTypeMetadata.ArrayType(candidates, element));
    }
}
