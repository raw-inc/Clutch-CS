using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>選択した処理で使う設定欄だけを表示します。非表示の保存値は変更しません。</summary>
public static class NodeSettingVisibility
{
    /// <summary>nullを明示値として選択できる設定かを確認します。</summary>
    /// <returns>未指定とは別にnullを許す場合はtrueです。</returns>
    public static bool AllowsNull(JsonElement schema)
    {
        string typeKey = "type";
        string nullType = "null";
        return schema.TryGetProperty(typeKey, out JsonElement type) && type.ValueKind == JsonValueKind.Array
            && type.EnumerateArray().Any(item => string.Equals(item.GetString(), nullType, StringComparison.Ordinal));
    }

    /// <summary>関連する選択値に合わせて、選択肢を絞った表示用スキーマを返します。</summary>
    /// <returns>選択が未確定なら元の選択肢を保持します。</returns>
    public static JsonElement Choices(JsonElement property, JsonElement settings)
    {
        string choicesKey = "x-choicesWhen";
        if (!property.TryGetProperty(choicesKey, out JsonElement condition))
        {
            return property;
        }
        string selector = condition.GetProperty("property").GetString()!;
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(selector, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return property;
        }
        string optionsKey = "options";
        if (!condition.GetProperty(optionsKey).TryGetProperty(value.GetString()!, out JsonElement choices))
        {
            return property;
        }
        JsonObject result = JsonNode.Parse(property.GetRawText())!.AsObject();
        result["enum"] = JsonNode.Parse(choices.GetRawText());
        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>スキーマに明示した表示条件を評価します。</summary>
    /// <returns>条件がない場合や選択値が不明な場合は表示します。</returns>
    public static bool IsVisible(JsonElement property, JsonElement settings)
    {
        string conditionKey = "x-visibleWhen";
        if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty(conditionKey, out JsonElement condition))
        {
            return true;
        }
        return Matches(condition, settings) ?? true;
    }

    /// <summary>選択した操作で必要になる設定欄を判定します。</summary>
    /// <returns>未選択の条件では必須と断定しません。</returns>
    public static bool IsRequired(JsonElement property, JsonElement settings)
    {
        string key = "x-requiredWhen";
        return property.TryGetProperty(key, out JsonElement condition) && Matches(condition, settings) == true;
    }

    /// <summary>表示条件と必須表示で同じ選択値を使います。</summary>
    /// <returns>選択値が未確定の場合はnullです。</returns>
    private static bool? Matches(JsonElement condition, JsonElement settings)
    {
        string field = condition.GetProperty("property").GetString()!;
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(field, out JsonElement value))
        {
            string fallbackKey = "default";
            if (!condition.TryGetProperty(fallbackKey, out value))
            {
                return null;
            }
        }
        return condition.GetProperty("values").EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value));
    }
}
