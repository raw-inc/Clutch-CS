using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>配列または辞書からの選択型を、実行せずに推測します。</summary>
internal static class ArrayPickTypes
{
    /// <summary>固定されたIndexと配列からは対象要素だけを、それ以外は共通の要素型を推測します。</summary>
    /// <returns>実行を伴わずに分かる選択要素の型です。</returns>
    internal static ValueTypeDefinition? Infer(NodeDefinition node, WorkflowLiteralValues literals, Func<string, string, bool, ValueTypeDefinition?> portType)
    {
        NodeValue? array = literals.Input(node, "value");
        NodeValue? index = literals.Input(node, "index");
        if (array?.Kind == EValueKind.Json && array.Json.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            && index?.Kind == EValueKind.Json && index.Json.ValueKind == JsonValueKind.Number
            && index.Json.TryGetDecimal(out decimal number) && number == decimal.Truncate(number)
            && 0 <= number && number < Count(array))
        {
            NodeValue selected = array.Json.ValueKind == JsonValueKind.Array ? Serialization.JsonPointer.Select(array, "/" + ((int)number).ToString(System.Globalization.CultureInfo.InvariantCulture))
                                                                             : DictionaryValues.Select(array, (int)number);
            return PathTypeMetadata.TypeOf(selected);
        }
        return Element(portType(node.Id, "value", false));
    }

    /// <summary>固定候補の範囲だけを読み、未知の候補を評価しません。</summary>
    /// <returns>配列要素または辞書項目の数です。</returns>
    private static int Count(NodeValue value) => value.Json.ValueKind switch
    {
        JsonValueKind.Array => value.Json.GetArrayLength(),
        JsonValueKind.Object => value.Json.EnumerateObject().Count(),
        _ => 0,
    };

    /// <summary>辞書からの選択では値だけでなくKey-Value一組を返します。</summary>
    /// <returns>配列要素または辞書の一組の型です。</returns>
    internal static ValueTypeDefinition? Element(ValueTypeDefinition? type) => type?.Type switch
    {
        "array" => type.ElementType,
        "object" => DictionaryValues.ItemType(type),
        _ => null,
    };
}
