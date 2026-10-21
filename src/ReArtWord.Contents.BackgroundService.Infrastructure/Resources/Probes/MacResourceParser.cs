using System.Globalization;
using System.Text.RegularExpressions;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>macOS標準コマンドの実測出力を単位付き値へ検証変換します。</summary>
public static class MacResourceParser
{
    /// <summary>topの二回目の差分標本を使い、起動以来の初回値を使用しません。</summary>
    /// <returns>CPU使用率。標本不足や不正値なら取得不能です。</returns>
    public static ResourceMetric Cpu(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        MatchCollection samples = Regex.Matches(output, @"CPU usage:\s*([0-9.]+)% user,\s*([0-9.]+)% sys,\s*([0-9.]+)% idle", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (samples.Count < 2)
        {
            return new("cpu.usage", "machine", null, "percent", "resource.cpuUnavailable");
        }
        Match sample = samples[^1];
        double[] values = new double[3];
        for (int index = 0; index < values.Length; index++)
        {
            if (!double.TryParse(sample.Groups[index + 1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0 || 100 < value)
            {
                return new("cpu.usage", "machine", null, "percent", "resource.cpuUnavailable");
            }
            values[index] = value;
        }
        if (0.1 < Math.Abs(values.Sum() - 100))
        {
            return new("cpu.usage", "machine", null, "percent", "resource.cpuUnavailable");
        }
        return new("cpu.usage", "machine", 100 - values[2], "percent");
    }

    /// <summary>実page sizeとhw.memsizeを使用し、freeとspeculativeを空きとして返します。</summary>
    /// <returns>inactiveは解放済みと捏造せず独立した値にします。不正形式なら例外です。</returns>
    public static IReadOnlyList<ResourceMetric> Memory(string statistics, string totalBytes)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(totalBytes);
        Match header = Regex.Match(statistics, @"page size of (\d+) bytes", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!header.Success || !long.TryParse(header.Groups[1].Value, CultureInfo.InvariantCulture, out long pageSize) || pageSize <= 0 || !long.TryParse(totalBytes.Trim(), CultureInfo.InvariantCulture, out long total) || total <= 0)
        {
            throw new InvalidDataException("macOSメモリ標本の単位を確認できません。");
        }
        Dictionary<string, long> pages = new(StringComparer.Ordinal);
        foreach (string line in statistics.Split('\n'))
        {
            int separator = line.IndexOf(':', StringComparison.Ordinal);
            if (0 < separator && long.TryParse(line[(separator + 1)..].Trim().TrimEnd('.'), NumberStyles.None, CultureInfo.InvariantCulture, out long count))
            {
                pages[line[..separator].Trim()] = count;
            }
        }
        long free = checked((Required(pages, "Pages free") + Required(pages, "Pages speculative")) * pageSize);
        long inactive = checked(Required(pages, "Pages inactive") * pageSize);
        if (total < free || total < inactive)
        {
            throw new InvalidDataException("macOSメモリ標本が実メモリ総量と矛盾しています。");
        }
        return [new("memory.total", "machine", total, "bytes"), new("memory.available", "machine", free, "bytes"), new("memory.used", "machine", total - free, "bytes"), new("memory.inactive", "machine", inactive, "bytes"), new("memory.usage", "machine", (total - free) * 100d / total, "percent")];
    }

    /// <summary>欠損値をゼロにせず必要なカウンターを取得します。</summary>
    /// <returns>確認したpage数です。</returns>
    private static long Required(Dictionary<string, long> pages, string key)
    {
        if (!pages.TryGetValue(key, out long count))
        {
            throw new InvalidDataException("macOSメモリ標本の必要項目がありません。");
        }
        return count;
    }
}
