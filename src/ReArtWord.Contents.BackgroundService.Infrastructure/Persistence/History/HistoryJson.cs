using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>内部保存と公開契約の項目名の違いを、履歴の分類境界で吸収します。</summary>
internal static class HistoryJson
{
    /// <summary>PascalCaseとcamelCaseのどちらも、同じ定義済みの項目として読みます。</summary>
    /// <returns>項目が存在するかです。</returns>
    internal static bool TryGet(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (value.TryGetProperty(name, out property))
        {
            return true;
        }
        Span<char> camel = stackalloc char[name.Length];
        name.AsSpan().CopyTo(camel);
        camel[0] = char.ToLowerInvariant(camel[0]);
        return value.TryGetProperty(camel, out property);
    }

    /// <summary>両方のJSON契約の結果不明を識別します。</summary>
    /// <returns>未確定の結果かです。</returns>
    internal static bool Unknown(JsonElement value)
    {
        string unknown = nameof(EOutcome.Unknown);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int outcome) && outcome == (int)EOutcome.Unknown
            || value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), unknown, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>不正な結果表現を完了扱いしません。</summary>
    /// <returns>定義済みの確定結果かです。</returns>
    internal static bool Final(JsonElement value)
    {
        EOutcome outcome;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            outcome = (EOutcome)number;
        }
        else
        {
            if (value.ValueKind != JsonValueKind.String || !Enum.TryParse(value.GetString(), ignoreCase: true, out outcome))
            {
                return false;
            }
        }
        return Enum.IsDefined(outcome) && outcome != EOutcome.Unknown;
    }
}
