using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>設定欄の入力文字列を、編集種類に対応するJSON値へ変換します。</summary>
internal static class SettingInputValues
{
    /// <summary>テキストとJSON、待受ポートの専用表記を区別して検証します。</summary>
    /// <returns>保存する値。不正な入力は例外にして元の保存値を維持します。</returns>
    internal static JsonNode? Parse(string raw, string type)
    {
        switch (type)
        {
            case "string":
                return JsonValue.Create(raw);
            case "listen-ports":
                return JsonNode.Parse(SettingChoiceValues.ParseListenPorts(raw).GetRawText());
        }
        JsonNode? parsed = JsonNode.Parse(raw);
        bool valid = type switch
        {
            "integer" => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) && decimal.Truncate(number) == number,
            "number" => parsed?.GetValueKind() == JsonValueKind.Number,
            "boolean" => parsed?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "object" => parsed?.GetValueKind() == JsonValueKind.Object,
            "array" => parsed?.GetValueKind() == JsonValueKind.Array,
            _ => true,
        };
        if (!valid)
        {
            throw new FormatException("The input type does not match.");
        }
        return parsed;
    }
}
