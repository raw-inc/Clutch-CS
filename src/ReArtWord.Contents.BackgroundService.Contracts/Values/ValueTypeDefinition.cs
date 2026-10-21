using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>値の構築と入力検証で使用する型定義です。元のJSONスキーマを追加で保持できます。</summary>
/// <param name="Type">any、boolean、バイト、integer、明示64bitのint64、number、decimal、char、string、path、array、オブジェクトです。</param>
/// <param name="ElementType">配列の要素型です。</param>
/// <param name="Properties">オブジェクトのプロパティ型。値の補足情報では配列の添字ごとの型も保持します。</param>
/// <param name="Schema">元のJSONスキーマ。型の表示とは別に制約全体を実行時に検証します。</param>
/// <param name="Optional">nullまたはプロパティの省略を許可します。</param>
/// <param name="KeyType">辞書のキー型。JSONのプロパティ名への表現方法も定めます。</param>
/// <param name="ValueType">辞書の各値に共通する型です。</param>
public sealed record ValueTypeDefinition(string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueTypeDefinition? ElementType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ValueTypeDefinition>? Properties = null,
    bool Optional = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] System.Text.Json.JsonElement? Schema = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueTypeDefinition? KeyType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueTypeDefinition? ValueType = null);
