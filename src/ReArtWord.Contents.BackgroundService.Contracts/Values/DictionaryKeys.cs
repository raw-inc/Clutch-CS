using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>JSONプロパティ名を、損失やキー衝突のないスクリプト辞書キーへ対応させます。</summary>
public static class DictionaryKeys
{
    /// <summary>文字列・整数・真偽値だけを言語間で共通に扱います。</summary>
    /// <returns>非nullのスカラーキーとして表現可能かです。</returns>
    public static bool Supported(ValueTypeDefinition? type) => type is not null && !type.Optional && type.Type is "string" or "char" or "integer" or "int64" or "byte" or "boolean"
        && type.ElementType is null && type.Properties is null && type.KeyType is null && type.ValueType is null && type.Schema is null;

    /// <summary>01と1など、変換後に衝突する別表記を暗黙に同一視しません。</summary>
    /// <returns>正規表記に対応するキーの値です。</returns>
    public static NodeValue Read(string name, ValueTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        if (!Supported(type))
        {
            throw new FormatException("dictionary.keyType");
        }
        TextValues.ValidateUnicode(name);
        switch (type.Type)
        {
            case "string": return NodeValue.FromObject(name);
            case "char" when name.Length == 1 && !char.IsSurrogate(name[0]): return NodeValue.FromObject(name);
            case "boolean" when name is "true" or "false": return NodeValue.FromObject(name is "true");
            case "integer" or "int64" or "byte":
                bool signedInteger = type.Type is "integer" or "int64";
                if (long.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer)
                    && string.Equals(name, integer.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    && (signedInteger || integer is >= 0 and <= 255))
                {
                    return PathTypeMetadata.Apply(NodeValue.FromObject(integer), type);
                }
                break;
        }
        throw new FormatException("dictionary.key.invalid");
    }
}
