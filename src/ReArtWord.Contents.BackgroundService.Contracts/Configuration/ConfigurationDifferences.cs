using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration;

/// <summary>比較画面に返す実差分の件数と値の表示量を制限します。</summary>
public static class ConfigurationDifferences
{
    private static int MaximumItems => 128;
    private static int MaximumValueLength => 256;

    /// <summary>入れ子の項目を比較し、秘密に該当する名前の値は表示しません。</summary>
    /// <returns>取得できた差分と、全項目を表示できたかです。</returns>
    public static (IReadOnlyList<ConfigurationDifference> Differences, bool Truncated) Compare<T>(T before, T after, bool hideValues = false)
    {
        JsonElement previous = JsonSerializer.SerializeToElement(before, ContractJson.Options);
        JsonElement current = JsonSerializer.SerializeToElement(after, ContractJson.Options);
        List<ConfigurationDifference> changes = [];
        bool truncated = false;
        Append(previous, current, string.Empty, hideValues, changes, ref truncated);
        return (changes, truncated);
    }

    /// <summary>オブジェクト項目を名前で照合し、取得上限後に残りを走査しません。</summary>
    private static void Append(JsonElement before, JsonElement after, string path, bool hidden, List<ConfigurationDifference> changes, ref bool truncated)
    {
        if (before.ValueKind != JsonValueKind.Undefined && after.ValueKind != JsonValueKind.Undefined && JsonElement.DeepEquals(before, after))
        {
            return;
        }
        if (MaximumItems <= changes.Count)
        {
            truncated = true;
            return;
        }
        bool objectPair = before.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined
            && after.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined
            && (before.ValueKind == JsonValueKind.Object || after.ValueKind == JsonValueKind.Object);
        if (objectPair)
        {
            Dictionary<string, JsonElement> previous = Properties(before);
            Dictionary<string, JsonElement> current = Properties(after);
            foreach (string key in previous.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                bool secret = hidden || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("apiKey", StringComparison.OrdinalIgnoreCase);
                Append(previous.GetValueOrDefault(key), current.GetValueOrDefault(key), path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), secret, changes, ref truncated);
                if (truncated)
                {
                    return;
                }
            }
            if (previous.Count != 0 || current.Count != 0)
            {
                return;
            }
        }
        bool arrayPair = before.ValueKind is JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined
            && after.ValueKind is JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined
            && (before.ValueKind == JsonValueKind.Array || after.ValueKind == JsonValueKind.Array);
        if (arrayPair)
        {
            int previousLength = ArrayLength(before), currentLength = ArrayLength(after);
            for (int index = 0; index < Math.Max(previousLength, currentLength); index++)
            {
                JsonElement previous = default, current = default;
                if (index < previousLength)
                {
                    previous = before[index];
                }
                if (index < currentLength)
                {
                    current = after[index];
                }
                Append(previous, current, path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), hidden, changes, ref truncated);
                if (truncated)
                {
                    return;
                }
            }
            if (previousLength != 0 || currentLength != 0)
            {
                return;
            }
        }
        string? oldValue = Display(before, hidden, out bool oldOmitted);
        string? newValue = Display(after, hidden, out bool newOmitted);
        changes.Add(new(path, oldValue, newValue, oldOmitted || newOmitted));
    }

    /// <summary>追加・削除されたobjectの具体的な変更項目も列挙します。</summary>
    /// <returns>objectではない欠落・null側は空です。</returns>
    private static Dictionary<string, JsonElement> Properties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return new(StringComparer.Ordinal);
        }
        return value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
    }

    /// <summary>追加・削除の欠落側を配列アクセスへ渡しません。</summary>
    /// <returns>配列の要素数、または0です。</returns>
    private static int ArrayLength(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.GetArrayLength();
        }
        return 0;
    }

    /// <summary>値の省略を明示し、長大な本文を管理応答へ載せません。</summary>
    /// <returns>制限した表示値です。</returns>
    private static string? Display(JsonElement value, bool hidden, out bool omitted)
    {
        omitted = false;
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (hidden || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            omitted = true;
            return "[value omitted]";
        }
        string text = value.GetRawText();
        if (MaximumValueLength < text.Length)
        {
            omitted = true;
            return text[..MaximumValueLength] + "…";
        }
        return text;
    }
}
