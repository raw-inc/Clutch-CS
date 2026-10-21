using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>レベル共通の保持とローリング日付を固定します。</summary>
internal sealed class LogPolicy
{
    private readonly Dictionary<string, LogRetentionRule> retention;
    private readonly TimeOnly rolling;
    private readonly TimeZoneInfo zone;

    /// <summary>起動または更新時に設定の構造を検証します。</summary>
    internal LogPolicy(LogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PeerId);
        if (!Path.IsPathFullyQualified(options.Directory) || options.QueueBytes is < 1024 or > 268435456 || options.ListenPort is < 0 or > 65535)
        {
            throw new ArgumentException("ログ保存先、queue byte上限またはportが不正です。", nameof(options));
        }
        string timeFormat = "HH:mm";
        if (!TimeOnly.TryParseExact(options.RollingTime, timeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out rolling))
        {
            throw new ArgumentException("ローリング時刻はHH:mmで指定してください。", nameof(options));
        }
        zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        retention = new(StringComparer.Ordinal);
        string[] levels = ["DEBUG", "INFO", "WARN", "ERROR", "FATAL"];
        foreach (string level in levels)
        {
            retention.Add(level, new(level, 7, 30));
        }
        foreach (LogRetentionRule rule in options.Retention ?? [])
        {
            string level = NormalizeLevel(rule.Level);
            if (rule.RawDays < 0 || (rule.TotalDays is int total && total < rule.RawDays))
            {
                throw new ArgumentException("総保持日数は非圧縮日数以上または無期限が必要です。", nameof(options));
            }
            retention[level] = rule with { Level = level };
        }
        HashSet<string> targetPeers = new(StringComparer.Ordinal);
        foreach (LogForwardTarget target in options.ForwardTargets ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.PeerId);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.RemoteAddress);
            if (!targetPeers.Add(target.PeerId) || target.Port is < 1 or > 65535)
            {
                throw new ArgumentException("ログ転送先が不正です。", nameof(options));
            }
        }
        Options = options with { Directory = Path.GetFullPath(options.Directory), Retention = retention.Values.ToArray(), ForwardTargets = options.ForwardTargets?.ToArray() };
    }

    /// <summary>一回の処理に固定した設定です。</summary>
    internal LogOptions Options
    {
        get;
    }

    /// <summary>正式レベル表記へ揃え、不正なパス要素も拒否します。</summary>
    /// <returns>保存に使用するレベルです。</returns>
    internal static string NormalizeLevel(string level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        string normalized = level.ToUpperInvariant();
        return normalized switch
        {
            "DEBUG" or "INFO" or "WARN" or "ERROR" or "FATAL" => normalized,
            _ => throw new ArgumentException("ログlevelが不正です。", nameof(level)),
        };
    }

    /// <summary>共通timezoneと基準時刻からローリング日付を求めます。</summary>
    /// <returns>このログが属する日付です。</returns>
    internal DateOnly Date(DateTimeOffset time)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(time, zone);
        DateOnly date = new(local.Year, local.Month, local.Day);
        if (local.TimeOfDay < rolling.ToTimeSpan() && date.DayNumber != 0)
        {
            date = date.AddDays(-1);
        }
        return date;
    }

    /// <summary>同一レベルで共通の保持設定を取得します。</summary>
    /// <returns>現在の保持方針です。</returns>
    internal LogRetentionRule Rule(string level)
    {
        return retention[NormalizeLevel(level)];
    }
}
