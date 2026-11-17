using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>派生IDの生成方式を、再送や再開に使う元の要求と一緒に保存します。</summary>
public static class DerivedIdentityPayload
{
    private static string Format => "clutch-derived-uuid8-v1";

    /// <summary>旧形式の本文は変えず、新形式だけを方式付きの配列で包みます。</summary>
    /// <returns>元要求と生成方式を含む本文です。</returns>
    public static JsonElement Write(JsonElement value, EDerivedIdFormat format)
    {
        if (format == EDerivedIdFormat.Legacy)
        {
            return value;
        }
        ArgumentOutOfRangeException.ThrowIfNotEqual(format, EDerivedIdFormat.Uuid8);

        // オブジェクトへの項目追加だけでは、旧版が未知の項目を無視して旧IDを再計算できます。
        // 配列は旧版の要求型として読めないため、別IDで副作用を開始する前に拒否されます。
        return JsonSerializer.SerializeToElement(new object[] { Format, value }, ContractJson.Options);
    }

    /// <summary>方式のない旧要求はそのまま読み、未知の保存形式は推測で変換しません。</summary>
    /// <returns>生成時の方式と元の本文です。</returns>
    public static (EDerivedIdFormat Format, JsonElement Value) Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return (EDerivedIdFormat.Legacy, value);
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2
            || value[0].ValueKind != JsonValueKind.String || !string.Equals(value[0].GetString(), Format, StringComparison.Ordinal)
            || value[1].ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("保存された操作のID生成方式に対応していません。");
        }
        return (EDerivedIdFormat.Uuid8, value[1]);
    }
}
