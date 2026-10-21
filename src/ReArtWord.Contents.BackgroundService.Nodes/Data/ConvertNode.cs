using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>スカラー変換を明示ノードに限定し、失敗を正常値へ丸めません。</summary>
public sealed class ConvertNode : IRuntimeValueNode
{
    /// <summary>変換先を設定し、出力はJSON スカラーとして公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.convert", "Convert value", "Data",
        """{"type":"object","required":["targetType"],"properties":{"targetType":{"type":"string","enum":["string","path","number","integer","int64","boolean"],"description":"Explicit scalar conversion. Number uses finite binary64; integer uses exact Int64; boolean accepts true/false or exact 0/1."}}}""",
        inputs: [new("value", Required: true), new("format", EPortValueKind.Json)],
        outputs: [new PortDescriptor("value", EPortValueKind.Json)]) with { IsValueOperation = true, Description = "Explicitly convert a JSON scalar. Null, objects, arrays, invalid values and out-of-range numbers fail." };

    /// <summary>共通スキーマを通じ変換先の指定を確認します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>入力の一回を、指定した型へ変換します。</summary>
    /// <returns>変換されたJSONまたは具体的な失敗理由です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeValue? input = NodeSupport.Input(context);
        if (input is null || input.Kind != EValueKind.Json || input.Json.ValueKind is JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
        {
            return ValueTask.FromResult(NodeResult.Failure("data.convert.input.type", "A non-null JSON scalar is required."));
        }
        try
        {
            string? target = new NodeSettings(context.Settings).ReadText("targetType");
            NodeValue output = target switch
            {
                "string" => NodeValue.FromObject(ScalarTextFormat.Convert(input, context.Inputs.GetValueOrDefault("format"))),
                "path" when input.Json.ValueKind == JsonValueKind.String => PathValues.Create(input.Json.GetString()!),
                "number" => NodeValue.FromObject(ScalarNumbers.Parse(ScalarNumbers.Token(input))),
                "integer" or "int64" => PathTypeMetadata.Apply(NodeValue.FromObject(ScalarNumbers.Integer(ScalarNumbers.Token(input))), new(target)),
                "boolean" => NodeValue.FromObject(Boolean(input)),
                _ => throw new FormatException("target.invalid"),
            };
            return ValueTask.FromResult(NodeResult.Success(output));
        }
        catch (FormatException error)
        {
            return ValueTask.FromResult(NodeResult.Failure("data.convert." + error.Message, "The value cannot be converted to the selected scalar type."));
        }
    }

    /// <summary>truthinessを使わずtrue/falseまたは正確な0/1だけを受け入れます。</summary>
    /// <returns>明示した真偽値です。</returns>
    internal static bool Boolean(NodeValue value)
    {
        if (value.Json.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.Json.GetBoolean();
        }
        if (value.Json.ValueKind == JsonValueKind.String && bool.TryParse(value.Json.GetString()!.Trim(), out bool parsed))
        {
            return parsed;
        }
        long number = ScalarNumbers.Integer(ScalarNumbers.Token(value));
        return number switch
        {
            0 => false,
            1 => true,
            _ => throw new FormatException("boolean.range"),
        };
    }
}
