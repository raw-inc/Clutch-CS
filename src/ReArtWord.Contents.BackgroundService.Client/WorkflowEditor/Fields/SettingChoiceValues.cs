using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>候補から消えた値を暗黙選択や削除へ変えません。</summary>
internal static class SettingChoiceValues
{
    /// <summary>異なるJSON種類を受ける設定にはJSON入力欄を使います。</summary>
    /// <returns>nullableだけの単一型は既存の専用入力欄を維持します。</returns>
    internal static string EditorType(JsonElement schema)
    {
        string editorKey = "x-editor";
        string listenPorts = "listen-ports";
        if (schema.TryGetProperty(editorKey, out JsonElement editor) && editor.ValueKind == JsonValueKind.String
            && string.Equals(editor.GetString(), listenPorts, StringComparison.Ordinal))
        {
            return listenPorts;
        }
        string typeKey = "type";
        if (!schema.TryGetProperty(typeKey, out JsonElement type))
        {
            return "json";
        }
        if (type.ValueKind != JsonValueKind.Array)
        {
            return type.GetString() ?? "json";
        }
        string nullType = "null";
        JsonElement[] alternatives = type.EnumerateArray().Where(item => !string.Equals(item.GetString(), nullType, StringComparison.Ordinal)).ToArray();
        return alternatives.Length switch { 1 => alternatives[0].GetString()!, _ => "json" };
    }

    /// <summary>待受ポートを通常の文字入力で編集し、既存の整数・整数配列も維持します。</summary>
    /// <returns>受信側と同じ規則で検証した保存値です。</returns>
    internal static JsonElement ParseListenPorts(string raw)
    {
        JsonElement value = JsonSerializer.SerializeToElement(raw);
        if (raw.TrimStart().StartsWith('['))
        {
            value = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        else
        {
            if (int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port))
            {
                value = JsonSerializer.SerializeToElement(port);
            }
        }
        _ = Contracts.Nodes.ListenPorts.Read(value);
        return value;
    }

    /// <summary>一致する候補だけを返します。</summary>
    /// <returns>候補を確認できた場合はtrueです。</returns>
    internal static bool TryEnum(JsonElement choices, string raw, out JsonElement selected)
    {
        selected = default;
        if (choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (JsonElement item in choices.EnumerateArray())
        {
            if (string.Equals(Text(item), raw, StringComparison.Ordinal))
            {
                selected = item.Clone();
                return true;
            }
        }
        return false;
    }

    /// <summary>未設定と候補の欠落を区別します。</summary>
    /// <returns>保存値が候補にない場合はtrueです。</returns>
    internal static bool Missing(string raw, JsonElement choices) => raw.Length != 0 && !TryEnum(choices, raw, out _);

    /// <summary>選択値はJSON型を維持した表記にします。</summary>
    /// <returns>機械値を変えない表示キーです。</returns>
    private static string Text(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }
        return value.GetRawText();
    }
}
