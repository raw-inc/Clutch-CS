using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>宣言された値区分と共通ポートの役割を区別します。</summary>
/// <param name="Name">実際のポート名です。</param>
/// <param name="Key">色と役割の識別子です。</param>
/// <param name="Label">小さく表示する型または役割です。</param>
/// <param name="Description">確認可能な契約の説明です。</param>
/// <param name="DataType">配列要素と省略可能性を含む、表示に使う値の型です。</param>
internal sealed record GraphPortPresentation(string Name, string Key, string Label, string Description, ValueTypeDefinition? DataType = null)
{
    private static readonly string[] dataKinds = ["any", "json", "unknown", "extracted", "binary"];

    /// <summary>データの種類に色と名前を合わせ、制御用ポートの役割は維持します。</summary>
    /// <returns>型が分からない場合は現在の表示です。</returns>
    internal GraphPortPresentation WithType(ValueTypeDefinition? type)
    {
        string definition = "type definition";
        string binary = "binary";
        string any = "any";
        string nullType = "null";
        if (type is null || DataType is null && !dataKinds.Contains(Key, StringComparer.Ordinal)
            || !ValueTypeInference.KnownName(type.Type) && !string.Equals(type.Type, definition, StringComparison.Ordinal) && !string.Equals(type.Type, binary, StringComparison.Ordinal) && !string.Equals(type.Type, nullType, StringComparison.Ordinal) && !string.Equals(type.Type, PcSelectionPorts.TypeName, StringComparison.Ordinal))
        {
            return this;
        }
        if (string.Equals(type.Type, any, StringComparison.Ordinal) && !type.Optional)
        {
            return this;
        }
        string family = type.Type switch { "byte" or "int64" => "integer", "decimal" => "number", "char" => "string", "type definition" => "type-definition", "null" => "any", _ => type.Type };
        return this with { Key = family, Label = "port.type." + type.Type, DataType = type, Description = "port.description.inferred" };
    }

    /// <summary>確認できた値の種類です。保存形式のJSONとは区別します。</summary>
    internal string? InferredType => DataType?.Type;

    /// <summary>型名の翻訳を動的ラベルから分離します。</summary>
    /// <returns>宣言と補足を区別した表示名です。</returns>
    internal string DisplayLabel(UiText text)
    {
        if (DataType is null)
        {
            return text[Label];
        }
        return TypeName(text, DataType, 0);
    }

    /// <summary>深い配列は有限の表示にし、nullを許す型も種類を隠さず表示します。</summary>
    /// <returns>表示言語に応じた値の型名です。</returns>
    private static string TypeName(UiText text, ValueTypeDefinition type, int depth)
    {
        string array = "array";
        string any = "any";
        string label = text["port.type." + type.Type];
        if (depth < 3 && type.KeyType is { } keyType && type.ValueType is { } valueType)
        {
            label = text.Format("port.type.dictionaryOf", TypeName(text, keyType, depth + 1), TypeName(text, valueType, depth + 1));
        }
        if (depth < 3 && string.Equals(type.Type, array, StringComparison.Ordinal) && type.ElementType is { } element && !string.Equals(element.Type, any, StringComparison.Ordinal))
        {
            label = text.Format("port.type.arrayOf", TypeName(text, element, depth + 1));
        }
        if (type.Optional)
        {
            label = text.Format("port.type.optional", label);
        }
        return label;
    }
}
