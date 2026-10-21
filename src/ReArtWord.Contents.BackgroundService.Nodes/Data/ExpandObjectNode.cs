using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>一階層の項目を既存のプロパティ出力へ公開し、入れ子は次の展開ノードへ渡します。</summary>
public sealed class ExpandObjectNode : PureValueNode
{
    /// <summary>解析済みObjectを受け取り、自動・スキーマ・手動の型指定を共用します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.object.expand", "Expand object", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", EPortValueKind.Json, true)], outputs: [new("value", EPortValueKind.Json)])
        with
        {
            SettingsSchema = StructuredValueTypes.Schema(JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), "object"),
            Description = "Expose object properties as individual outputs. Connect a nested object to another Expand object node. Use a schema or manual fields when the input structure is unknown."
        };

    /// <summary>文字列や配列を暗黙変換せず、明示型があれば元の制約を検証します。</summary>
    /// <returns>複製せず個別出力から参照するObjectです。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string port = "value";
        if (!inputs.TryGetValue(port, out NodeValue? value))
        {
            throw new FormatException("object.required");
        }
        ValueTypeOperations.ValidateValue(value, new("object"));
        string prefix = "object";
        if (StructuredValueTypes.Read(definition.Settings, inputs, prefix) is { } type)
        {
            ValueTypeOperations.ValidateValue(value, type);
            value = PathTypeMetadata.Apply(value, type);
        }
        return value;
    }
}
