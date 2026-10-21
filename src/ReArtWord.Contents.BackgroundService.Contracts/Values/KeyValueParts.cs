using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>一組のKey-Valueから、対応を保ったままキーまたは値を取り出します。</summary>
public static class KeyValueParts
{
    /// <summary>複数項目の辞書から任意の一項目を勝手に選びません。</summary>
    /// <returns>元のキー型または値型を保った独立した値です。</returns>
    public static NodeValue Read(NodeValue? pair, bool key)
    {
        if (pair?.Kind != EValueKind.Json || pair.Json.ValueKind != JsonValueKind.Object || pair.Json.EnumerateObject().Count() != 1)
        {
            throw new FormatException("keyValue.singleEntry");
        }
        JsonProperty entry = pair.Json.EnumerateObject().First();
        return key ? DictionaryKeys.Read(entry.Name, pair.DataType?.KeyType ?? new("string"))
                   : JsonPointer.Select(pair, PropertyPorts.Name(string.Empty, entry.Name));
    }

    /// <summary>固定キーがなくても、辞書に宣言されたキー・値の型を使用します。</summary>
    /// <returns>型が混在する値はanyです。</returns>
    internal static ValueTypeDefinition Infer(ValueTypeDefinition? pair, bool key)
    {
        if (key)
        {
            return pair?.KeyType ?? new("string");
        }
        if (pair?.Properties?.Count == 1)
        {
            return pair.Properties.Values.First();
        }
        return pair?.ValueType ?? new("any");
    }
}
