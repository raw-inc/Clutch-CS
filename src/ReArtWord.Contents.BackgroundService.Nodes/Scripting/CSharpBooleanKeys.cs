using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>CLRの真偽値キーもPythonと同じJSONのtrue/false表記へ揃えます。</summary>
internal sealed class CSharpBooleanKeys : JsonConverter<bool>
{
    /// <summary>通常の真偽値の読み取りは変更しません。</summary>
    /// <returns>JSONの真偽値です。</returns>
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetBoolean();

    /// <summary>通常の真偽値をJSONへ書きます。</summary>
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);

    /// <summary>辞書のキーだけを明示的に読みます。</summary>
    /// <returns>正規表記のキーです。</returns>
    public override bool ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "true" => true,
        "false" => false,
        _ => throw new JsonException("dictionary.key.invalid"),
    };

    /// <summary>文字列キーの大文字小文字には影響を与えません。</summary>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        string name = "false";
        if (value)
        {
            name = "true";
        }
        writer.WritePropertyName(name);
    }
}
