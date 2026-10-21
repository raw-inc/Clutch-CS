using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>整数と小数が混在する進捗を同じ規則で検証・表示します。</summary>
/// <param name="Total">非負の総件数です。</param>
/// <param name="Current">非負の現在数です。</param>
public sealed record ProgressValue(double Total, double Current)
{
    /// <summary>総数ゼロは0%、超過分は表示数値を残してバーを100%に留めます。</summary>
    public double Percentage => Total == 0 ? 0
                                           : 100 * Math.Min(Current / Total, 1);

    /// <summary>文字列や非有限値を数値として読み替えません。</summary>
    /// <returns>両方とも非負の有限数値なら進捗です。</returns>
    public static ProgressValue? Read(NodeValue? total, NodeValue? current)
    {
        if (total?.Kind != EValueKind.Json || current?.Kind != EValueKind.Json
            || total.Json.ValueKind != System.Text.Json.JsonValueKind.Number || current.Json.ValueKind != System.Text.Json.JsonValueKind.Number
            || !total.Json.TryGetDouble(out double maximum) || !current.Json.TryGetDouble(out double value)
            || !double.IsFinite(maximum) || !double.IsFinite(value) || maximum < 0 || value < 0)
        {
            return null;
        }
        return new(maximum, value);
    }
}
