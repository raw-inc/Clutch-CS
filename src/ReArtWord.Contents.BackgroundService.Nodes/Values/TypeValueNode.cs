using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>データ値と区別した明示型オブジェクトを構築します。</summary>
public sealed class TypeValueNode : PureValueNode
{
    /// <summary>elementTypeとpropertiesは他の値ノードから接続できます。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.type", "Value type", "Values",
        """{"type":"object","required":["type"],"properties":{"type":{"x-runtime-input":false,"type":"string","enum":["any","boolean","byte","integer","int64","number","decimal","char","string","path","array","object"]},"optional":{"type":"boolean"}}}""",
        inputs: [new("elementType", EPortValueKind.Json), new("properties", EPortValueKind.Json)], outputs: [new("value", EPortValueKind.Json)]) with { Description = "Build a type descriptor with optional array element and object property definitions. This is not JSON Schema." };

    /// <summary>入れ子の型定義を同じ共通validatorで確認します。</summary>
    /// <returns>camelCaseの型定義JSONです。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string elementTypePort = "elementType";
        string propertiesPort = "properties";
        NodeSettings settings = new(definition.Settings);
        ValueTypeDefinition? element = null;
        Dictionary<string, ValueTypeDefinition>? properties = null;
        if (inputs.TryGetValue(elementTypePort, out NodeValue? supplied))
        {
            element = ValueTypeOperations.ReadType(supplied);
        }
        if (inputs.TryGetValue(propertiesPort, out NodeValue? source))
        {
            if (source.Kind != EValueKind.Json || source.Json.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("type.properties.object");
            }
            properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in source.Json.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, ValueTypeOperations.ReadType(NodeValue.FromJson(property.Value))))
                {
                    throw new FormatException("type.properties.duplicate");
                }
            }
        }
        ValueTypeDefinition type = new(settings.ReadText("type")!, element, properties, settings.ReadBoolean("optional"));
        ValueTypeOperations.ValidateType(type);
        return NodeValue.FromJson(JsonSerializer.SerializeToElement(type, ContractJson.Options));
    }
}
