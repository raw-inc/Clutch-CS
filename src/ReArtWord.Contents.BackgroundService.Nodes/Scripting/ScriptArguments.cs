using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>単一値・配列・未入力を各言語の可変長引数へ揃えます。</summary>
internal static class ScriptArguments
{
    /// <summary>可変長引数だけを配列にし、一般引数の型や値は変換しません。</summary>
    /// <returns>要素型の検証へ渡すJSON値です。</returns>
    internal static NodeValue Normalize(NodeValue value, string argument, ScriptContract contract)
    {
        if (!string.Equals(argument, contract.VariadicArgument, StringComparison.Ordinal))
        {
            return value;
        }
        if (value.Kind != EValueKind.Json)
        {
            throw new FormatException("script.argumentJsonRequired");
        }
        if (value.Json.ValueKind == JsonValueKind.Null)
        {
            return NodeValue.FromObject(Array.Empty<JsonElement>());
        }
        if (value.Json.ValueKind == JsonValueKind.Array)
        {
            return value;
        }
        return NodeValue.FromObject(new[] { value.Json });
    }

    /// <summary>一括・個別どちらの入力にも同じ可変長引数の規則を適用します。</summary>
    /// <returns>一般引数とPath型を維持した呼出し用オブジェクトです。</returns>
    internal static NodeValue NormalizeObject(NodeValue value, ScriptContract contract)
    {
        if (contract.VariadicArgument is not { } variadic || value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Object)
        {
            return value;
        }
        Dictionary<string, JsonElement> arguments = value.Json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        NodeValue supplied = NodeValue.Null;
        if (arguments.TryGetValue(variadic, out JsonElement present))
        {
            supplied = NodeValue.FromJson(present);
        }
        arguments[variadic] = Normalize(supplied, variadic, contract).Json;
        ValueTypeDefinition? metadata = value.DataType;
        if (present.ValueKind != JsonValueKind.Array && metadata?.Properties?.GetValueOrDefault(variadic) is { } element)
        {
            Dictionary<string, ValueTypeDefinition> properties = new(metadata.Properties, StringComparer.Ordinal)
            {
                [variadic] = new("array", element, Optional: true),
            };
            metadata = metadata with { Properties = properties };
        }
        return NodeValue.FromObject(arguments) with { DataType = metadata };
    }
}
