using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>ログと履歴に同じ日付境界と期間の意味を適用します。</summary>
public static class HistoryRetentionPolicy
{
    /// <summary>未指定の旧設定は既存ログの期間を継承します。</summary>
    /// <returns>検証済みの共通方針です。</returns>
    public static DataRetentionSettings Resolve(ClusterOperationalSettings settings, LogOptions? localLogs = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.DataRetention is { } configured)
        {
            return configured with { TimeZoneId = configured.TimeZoneId ?? settings.TimeZoneId };
        }
        LogRetentionSettings? previous = settings.LogRetention;
        IReadOnlyList<LogRetentionRule>? rules = previous?.Rules ?? localLogs?.Retention;
        LogRetentionRule? baseline = rules?.FirstOrDefault(rule => string.Equals(rule.Level, "INFO", StringComparison.Ordinal));
        Dictionary<ERetentionCategory, RetentionWindow> overrides = [];
        foreach (ERetentionCategory category in Enum.GetValues<ERetentionCategory>().Where(IsLog))
        {
            LogRetentionRule? rule = rules?.FirstOrDefault(rule => string.Equals(rule.Level, LogLevel(category), StringComparison.Ordinal));
            if (rule is not null)
            {
                overrides[category] = new(rule.RawDays, rule.TotalDays);
            }
        }
        int? total = 30;
        if (baseline is not null)
        {
            total = baseline.TotalDays;
        }
        return new(baseline?.RawDays ?? 7, total, overrides,
            RollingTime: previous?.RollingTime ?? localLogs?.RollingTime ?? "00:00", TimeZoneId: previous?.TimeZoneId ?? settings.TimeZoneId);
    }

    /// <summary>共通方針を既存のログ保存処理へ渡します。</summary>
    /// <returns>全レベルを明示したログ保持です。</returns>
    public static LogRetentionSettings Logs(DataRetentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(Enum.GetValues<ERetentionCategory>().Where(IsLog).Select(category => new LogRetentionRule(LogLevel(category), settings.Window(category).RawDays,
            settings.Window(category).TotalDays)).ToArray(), settings.RollingTime, settings.TimeZoneId!, settings.ApplyToExisting);
    }

    /// <summary>ローリング時刻を過ぎた最新のローカル日付をUTCへ戻します。</summary>
    /// <returns>すべての種類が共用する保持日数の起点です。</returns>
    // 抑制理由: DSTを含む現地の時刻をTimeZoneInfoへ渡す境界だけDateTimeを必要とします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW171", Justification = "TimeZoneInfoの現地時刻とDST検証APIがDateTimeを要求します。保存・比較・公開値はDateTimeOffsetです。")]
    internal static DateTimeOffset Boundary(DataRetentionSettings settings, DateTimeOffset now)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId ?? "UTC");
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        TimeOnly rolling = TimeOnly.ParseExact(settings.RollingTime, "HH:mm", CultureInfo.InvariantCulture);
        DateTime date = local.Date + rolling.ToTimeSpan();
        if (local < date)
        {
            date = date.AddDays(-1);
        }
        while (zone.IsInvalidTime(date))
        {
            date = date.AddMinutes(1);
        }
        return new(TimeZoneInfo.ConvertTimeToUtc(date, zone));
    }

    /// <summary>本文のない検索索引でも同じ用途を分類します。</summary>
    /// <returns>期限対象の種類、現在値ならnullです。</returns>
    internal static ERetentionCategory? Category(string kind, string? scope)
    {
        string receipt = "receipt", shared = "shared", snapshots = "snapshots";
        string detached = ":detached-snapshots", workflows = "workflow.history:", operational = "cluster.operational.history";
        string requests = "receipts:", executions = ":workflow-results", details = ":workflow-result-details", operations = "cluster:configuration-operations:", routing = "peer:persistence-routing";
        string replication = "cluster:operations", members = "cluster:replication-members";
        if (string.Equals(scope, Receipts.ReceiptReconciliation.AuditScope, StringComparison.Ordinal))
        {
            return ERetentionCategory.OperationHistory;
        }
        string manual = "manual-runs", localManual = ":manual-runs", updates = "updates";
        if (string.Equals(kind, receipt, StringComparison.Ordinal))
        {
            if (string.Equals(scope, shared, StringComparison.Ordinal))
            {
                return ERetentionCategory.SharedHistory;
            }
            return ERetentionCategory.OperationHistory;
        }
        if (string.Equals(scope, snapshots, StringComparison.Ordinal) || scope?.EndsWith(detached, StringComparison.Ordinal) == true)
        {
            return ERetentionCategory.Snapshots;
        }
        if (scope?.StartsWith(workflows, StringComparison.Ordinal) == true || string.Equals(scope, operational, StringComparison.Ordinal))
        {
            return ERetentionCategory.WorkflowVersions;
        }
        if (scope?.StartsWith(requests, StringComparison.Ordinal) == true)
        {
            return ERetentionCategory.RequestHistory;
        }
        if (scope?.EndsWith(executions, StringComparison.Ordinal) == true || scope?.EndsWith(details, StringComparison.Ordinal) == true
            || string.Equals(scope, manual, StringComparison.Ordinal) || scope?.EndsWith(localManual, StringComparison.Ordinal) == true)
        {
            return ERetentionCategory.ExecutionHistory;
        }
        if (scope?.StartsWith(operations, StringComparison.Ordinal) == true || string.Equals(scope, routing, StringComparison.Ordinal)
            || string.Equals(scope, replication, StringComparison.Ordinal) || string.Equals(scope, members, StringComparison.Ordinal) || string.Equals(scope, updates, StringComparison.Ordinal)
            || FileHistoryEligibility.IsScope(scope))
        {
            return ERetentionCategory.OperationHistory;
        }
        return null;
    }

    /// <summary>明示された完了日時だけを採用し、不明な旧行を作成時刻と推測しません。</summary>
    /// <returns>本文中の保存・取得日時、取得できなければ観測日時です。</returns>
    internal static DateTimeOffset RecordedAt(JsonElement value, DateTimeOffset observed)
    {
        foreach (string name in new[] { "CapturedAt", "SavedAt", "CompletedAt", "ExpiresAt" })
        {
            if (HistoryJson.TryGet(value, name, out JsonElement time)
                && time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out DateTimeOffset date))
            {
                return date;
            }
        }
        string snapshotName = "Snapshot";
        if (HistoryJson.TryGet(value, snapshotName, out JsonElement snapshot))
        {
            return RecordedAt(snapshot, observed);
        }
        string viewName = "View", reportName = "Report", summaryName = "Summary", completedName = "CompletedAt";
        if (HistoryJson.TryGet(value, viewName, out JsonElement view))
        {
            foreach (string name in new[] { reportName, summaryName })
            {
                if (HistoryJson.TryGet(view, name, out JsonElement report) && HistoryJson.TryGet(report, completedName, out JsonElement time)
                    && time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out DateTimeOffset date))
                {
                    return date;
                }
            }
        }
        return observed;
    }

    /// <summary>履歴の種類をログレベルと混同しません。</summary>
    /// <returns>既存ログ保持へ投影する種類かです。</returns>
    internal static bool IsLog(ERetentionCategory category) => category is ERetentionCategory.DebugLog or ERetentionCategory.InfoLog or ERetentionCategory.WarnLog or ERetentionCategory.ErrorLog or ERetentionCategory.FatalLog;

    /// <summary>既存のログレベル表記へ変換します。</summary>
    /// <returns>大文字のログレベルです。</returns>
    private static string LogLevel(ERetentionCategory category) => category.ToString()[..^3].ToUpperInvariant();
}
