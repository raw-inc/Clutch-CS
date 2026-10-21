using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Runtime.Configuration;

/// <summary>ノード設定の型を保って明示した既定値と区別して読み取ります。</summary>
public sealed class NodeSettings
{
    private readonly JsonElement settings;

    /// <summary>一回の設定を読み取るための参照を保持します。</summary>
    public NodeSettings(JsonElement settings)
    {
        this.settings = settings;
    }

    /// <summary>指定したJSON項目を取得します。</summary>
    /// <returns>未指定の場合はUndefinedです。</returns>
    public JsonElement Get(string name)
    {
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out JsonElement value))
        {
            return value;
        }
        return default;
    }

    /// <summary>文字列を取得します。</summary>
    /// <returns>指定値または既定値です。</returns>
    public string? ReadText(string name, string? fallback = null)
    {
        JsonElement value = Get(name);
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return fallback;
    }

    /// <summary>整数を取得します。</summary>
    /// <returns>指定値または既定値です。</returns>
    public int ReadInt32(string name, int fallback = 0)
    {
        JsonElement value = Get(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }
        return fallback;
    }

    /// <summary>実数を取得します。</summary>
    /// <returns>指定値または既定値です。</returns>
    public double ReadNumber(string name, double fallback = 0)
    {
        JsonElement value = Get(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }
        return fallback;
    }

    /// <summary>真偽値を取得します。</summary>
    /// <returns>指定値または既定値です。</returns>
    public bool ReadBoolean(string name, bool fallback = false)
    {
        JsonElement value = Get(name);
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }
        return fallback;
    }

    /// <summary>メタデータで使うJSON Schemaを文書寿命から独立して読み取ります。</summary>
    /// <returns>読み取った構造です。</returns>
    public static JsonElement Schema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
