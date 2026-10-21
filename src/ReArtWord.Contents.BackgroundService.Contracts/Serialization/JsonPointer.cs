using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Contracts.Serialization;

/// <summary>配線と抽出ノードで共用するRFC6901の値選択です。</summary>
public static class JsonPointer
{
    /// <summary>先頭区切りとエスケープの構文を検証します。</summary>
    /// <returns>有効なJSON Pointerかを示します。</returns>
    public static bool IsValid(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length != 0 && !path.StartsWith('/'))
        {
            return false;
        }
        for (int index = 0; index < path.Length; index++)
        {
            if (path[index] == '~')
            {
                if (path.Length <= index + 1 || path[index + 1] is not '0' and not '1')
                {
                    return false;
                }
                index++;
            }
        }
        return true;
    }

    /// <summary>存在しない項目を正常nullとして値を選択します。</summary>
    /// <returns>選択値。ポインター不正または非JSONの内部参照は失敗します。</returns>
    public static NodeValue Select(NodeValue input, string path)
    {
        if (TrySelect(input, path, out NodeValue? selected))
        {
            return selected!;
        }
        return NodeValue.Null;
    }

    /// <summary>存在するJSON nullと、存在しない項目を区別して選択します。</summary>
    /// <returns>参照先が存在すればtrueです。構文不正と非JSON内部参照は従来どおり失敗します。</returns>
    public static bool TrySelect(NodeValue input, string path, out NodeValue? selected)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(path);
        selected = null;
        if (!IsValid(path))
        {
            throw new FormatException("JSON Pointerの構文が不正です。");
        }
        if (path.Length == 0)
        {
            selected = input.Copy();
            return true;
        }
        if (input.Kind != EValueKind.Json)
        {
            throw new FormatException("バイト列の内部をJSON Pointerで参照できません。");
        }
        JsonElement value = input.Json;
        foreach (string encoded in path[1..].Split('/'))
        {
            string key = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!value.TryGetProperty(key, out JsonElement found))
                    {
                        return false;
                    }
                    value = found;
                    break;
                case JsonValueKind.Array:
                    if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                        || (1 < key.Length && key.StartsWith('0')) || value.GetArrayLength() <= index)
                    {
                        return false;
                    }
                    value = value[index];
                    break;
                default:
                    return false;
            }
        }
        selected = NodeValue.FromJson(value) with { DataType = PathTypeMetadata.Copy(ValueTypeInference.Select(input.DataType, path)) };
        return true;
    }
}
