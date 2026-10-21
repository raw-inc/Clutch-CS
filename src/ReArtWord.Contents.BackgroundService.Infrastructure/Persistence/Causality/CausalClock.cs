using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

/// <summary>書込み元の順序と分岐を保持し、壁時計の大小で新旧を決めません。</summary>
internal static class CausalClock
{
    /// <summary>外部入力を有限の書込み元集合へ検証します。</summary>
    /// <returns>名前順に正規化した確定位置です。</returns>
    internal static SortedDictionary<string, long> Read(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (262144 < value.Length)
        {
            throw new InvalidDataException("因果情報の容量が上限を超えています。");
        }
        SortedDictionary<string, long> clock = JsonSerializer.Deserialize<SortedDictionary<string, long>>(value)
            ?? throw new InvalidDataException("因果情報がありません。");
        if (clock.Count is < 1 or > 1024 || clock.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || 128 < pair.Key.Length || pair.Value <= 0))
        {
            throw new InvalidDataException("因果情報が不正です。");
        }
        return new(clock, StringComparer.Ordinal);
    }

    /// <summary>参照した親版の全位置を維持し、今回の書込み元だけを進めます。</summary>
    /// <returns>順序に依存しない新しい因果情報です。</returns>
    internal static string Advance(IEnumerable<string> parents, string origin, long sequence)
    {
        SortedDictionary<string, long> clock = Merge(parents);
        clock[origin] = Math.Max(checked(clock.GetValueOrDefault(origin) + 1), sequence);
        return JsonSerializer.Serialize(clock);
    }

    /// <summary>同じ内容が別経路を通った場合も、確認済みの位置を落としません。</summary>
    /// <returns>各書込み元の最大確認位置です。</returns>
    internal static SortedDictionary<string, long> Merge(IEnumerable<string> clocks)
    {
        SortedDictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (string value in clocks)
        {
            foreach (KeyValuePair<string, long> pair in Read(value))
            {
                result[pair.Key] = Math.Max(result.GetValueOrDefault(pair.Key), pair.Value);
            }
        }
        return result;
    }

    /// <summary>全書込み元の位置が包含される場合だけ祖先と判定します。</summary>
    /// <returns>先行または同一の確認位置かです。</returns>
    internal static bool Includes(string descendant, string ancestor)
    {
        SortedDictionary<string, long> current = Read(descendant);
        return Read(ancestor).All(pair => pair.Value <= current.GetValueOrDefault(pair.Key));
    }

    // LEGACY-FORMAT: repository-ancestry-v1 — 旧版を参照する現在値・分岐の移行確認後に旧版からの基準生成を撤去します。
    /// <summary>旧形式の現在版を、一意の移行基準として識別します。</summary>
    /// <returns>同じ旧版に対して同じ値になる移行基準です。</returns>
    internal static string Legacy(DurableEntry entry)
        => JsonSerializer.Serialize(new SortedDictionary<string, long>(StringComparer.Ordinal) { ["legacy:" + ContractJson.Hash(new { entry.Scope, entry.Key, entry.Version })] = 1 });
}
