using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>配列要素または辞書の一組を選び、存在するnullと範囲外を区別します。</summary>
internal static class CollectionItemSelection
{
    /// <summary>配列と辞書を受け取り、空の候補集合を拒否します。</summary>
    /// <returns>入力された配列または辞書です。</returns>
    internal static NodeValue Read(IReadOnlyDictionary<string, NodeValue> inputs)
    {
        NodeValue? value = inputs.GetValueOrDefault("value");
        if (value?.Kind != EValueKind.Json || value.Json.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            throw new FormatException("pick.input.collection");
        }
        if (Count(value) == 0)
        {
            throw new FormatException("pick.collection.empty");
        }
        return value;
    }

    /// <summary>入れ子へ降りず、選択可能な要素またはキーの数を取得します。</summary>
    /// <returns>配列の要素数または辞書の項目数です。</returns>
    internal static int Count(NodeValue value) => value.Json.ValueKind == JsonValueKind.Array ? value.Json.GetArrayLength()
                                                                                              : value.Json.EnumerateObject().Count();

    /// <summary>暗黙の丸め・負数の折り返しを行わず、0始まりで要素を取得します。</summary>
    /// <returns>Pathを含む型情報と所有権を保った要素です。</returns>
    internal static NodeValue Select(NodeValue collection, long index)
    {
        if (index < 0 || Count(collection) <= index)
        {
            throw new FormatException("pick.index.range");
        }
        return collection.Json.ValueKind == JsonValueKind.Array ? JsonPointer.Select(collection, "/" + index.ToString(CultureInfo.InvariantCulture))
                                                                : DictionaryValues.Select(collection, (int)index);
    }
}
