using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Configuration;

/// <summary>ノード設定と画面へ公開するスキーマを同じ規則で検証します。</summary>
public static class NodeSchemaValidator
{
    private static string Required => "required";
    private static string Properties => "properties";
    private static string Type => "type";
    private static string Options => "enum";
    private static string Minimum => "minimum";
    private static string Maximum => "maximum";
    private static string Items => "items";
    private static string MinimumItems => "minItems";
    private static string MaximumItems => "maxItems";
    private static string MinimumLength => "minLength";
    private static string MaximumLength => "maxLength";

    /// <summary>型、必須項目、選択肢、範囲と未知の設定項目を検証します。</summary>
    /// <returns>開始前に訂正が必要な設定です。</returns>
    public static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition, NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(descriptor);
        List<ValidationIssue> issues = [];
        if (definition.ListenDurationMs is not null && (definition.ListenDurationMs <= 0 || descriptor.Kind != ENodeKind.Source))
        {
            issues.Add(new("node.listenDuration.invalid", "待受の終了時間はSourceにだけ、正のミリ秒数または常時待受を表す未指定で設定してください。", definition.Id, "listenDurationMs"));
        }
        if (definition.Settings.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("settings.object", "設定はオブジェクトで指定してください。", definition.Id));
            return issues;
        }
        ValidateValue(definition.Settings, descriptor.SettingsSchema, definition.Id, string.Empty, issues);
        return issues;
    }

    /// <summary>ネストした値を同じ型・範囲規則で検証します。</summary>
    private static void ValidateValue(JsonElement value, JsonElement rule, string nodeId, string path, List<ValidationIssue> issues)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("schema.invalid", "ノードの設定スキーマが不正です。", nodeId, path));
            return;
        }
        bool valid = true;
        if (rule.TryGetProperty(Type, out JsonElement type))
        {
            valid = MatchesType(value, type);
        }
        if (valid && rule.TryGetProperty(Options, out JsonElement options))
        {
            valid = options.ValueKind == JsonValueKind.Array
                && options.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value));
        }
        if (valid && value.ValueKind == JsonValueKind.Number)
        {
            valid = ValidNumber(value, rule);
        }
        if (valid && value.ValueKind == JsonValueKind.String)
        {
            int length = value.GetString()!.Length;
            valid = ValidLength(length, rule, MinimumLength, MaximumLength);
        }
        if (valid && value.ValueKind == JsonValueKind.Array)
        {
            valid = ValidLength(value.GetArrayLength(), rule, MinimumItems, MaximumItems);
        }
        if (!valid)
        {
            issues.Add(new ValidationIssue("settings.value", "設定値の型・選択肢・範囲が一致しません。", nodeId, path));
            return;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, rule, nodeId, path, issues);
        }
        if (value.ValueKind == JsonValueKind.Array && rule.TryGetProperty(Items, out JsonElement itemRule))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValidateValue(item, itemRule, nodeId, string.Concat(path, "/", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), issues);
                index++;
            }
        }
    }

    /// <summary>オブジェクトの必須項目と定義された設定項目を検証します。</summary>
    private static void ValidateObject(JsonElement value, JsonElement rule, string nodeId, string path, List<ValidationIssue> issues)
    {
        if (rule.TryGetProperty(Required, out JsonElement required))
        {
            foreach (JsonElement item in required.EnumerateArray())
            {
                string name = item.GetString()!;
                if (!value.TryGetProperty(name, out _))
                {
                    issues.Add(new ValidationIssue("settings.required", "必須設定がありません。", nodeId, string.Concat(path, "/", name)));
                }
            }
        }
        if (!rule.TryGetProperty(Properties, out JsonElement properties))
        {
            return;
        }
        foreach (JsonProperty property in value.EnumerateObject())
        {
            string propertyPath = string.Concat(path, "/", property.Name);
            if (!properties.TryGetProperty(property.Name, out JsonElement propertyRule))
            {
                issues.Add(new ValidationIssue("settings.unknown", "未対応の設定項目です。", nodeId, propertyPath));
                continue;
            }
            string legacyKey = "x-legacy";
            bool legacy = propertyRule.TryGetProperty(legacyKey, out JsonElement ignored) && ignored.ValueKind == JsonValueKind.True;
            if (!legacy && NodeSettingVisibility.IsVisible(propertyRule, value))
            {
                ValidateValue(property.Value, propertyRule, nodeId, propertyPath, issues);
            }
        }
    }

    /// <summary>単一型またはnullを含む型集合を検証します。</summary>
    /// <returns>いずれかの型へ一致するかを示します。</returns>
    private static bool MatchesType(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(candidate => MatchesType(value, candidate));
        }
        return type.GetString() switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
    }

    /// <summary>有限の数値と指定された上下限を検証します。</summary>
    /// <returns>数値の条件を満たすかを示します。</returns>
    private static bool ValidNumber(JsonElement value, JsonElement rule)
    {
        if (!value.TryGetDouble(out double number) || !double.IsFinite(number))
        {
            return false;
        }
        return (!rule.TryGetProperty(Minimum, out JsonElement minimum) || minimum.GetDouble() <= number)
            && (!rule.TryGetProperty(Maximum, out JsonElement maximum) || number <= maximum.GetDouble());
    }

    /// <summary>文字列または配列の長さを検証します。</summary>
    /// <returns>指定された長さの条件を満たすかを示します。</returns>
    private static bool ValidLength(int length, JsonElement rule, string minimumName, string maximumName)
    {
        return (!rule.TryGetProperty(minimumName, out JsonElement minimum) || minimum.GetInt32() <= length)
            && (!rule.TryGetProperty(maximumName, out JsonElement maximum) || length <= maximum.GetInt32());
    }
}
