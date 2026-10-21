using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>場所に依存しない共通設定の構造と意味を確認します。</summary>
public static class ClusterConfigurationValidation
{
    /// <summary>以前に実適用を保存したローカル版だけを内容検査して復元します。</summary>
    /// <returns>保持する確認済み版、または利用不能です。</returns>
    internal static ClusterOperationalSnapshot? ReadConfirmed(NodeValue? value)
    {
        if (value?.Json.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        ClusterOperationalSnapshot? snapshot = value.Json.Deserialize<ClusterOperationalSnapshot>();
        if (snapshot is null || Validate(snapshot.Settings).Count != 0 || !string.Equals(snapshot.Revision, ContractJson.Hash(snapshot.Settings), StringComparison.Ordinal))
        {
            return null;
        }
        return snapshot;
    }

    /// <summary>未知enum、不正Groupと解決不能なtimezoneを拒否します。</summary>
    /// <returns>開始前に直す問題です。</returns>
    public static IReadOnlyList<ValidationIssue> Validate(ClusterOperationalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<ValidationIssue> issues = [];
        if (settings.DataRetention is { } dataRetention)
        {
            try
            {
                dataRetention.Validate();
            }
            catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                issues.Add(new("cluster.dataRetention.invalid", "ログ・履歴の保持期間または基準点の作成条件が不正です。"));
            }
        }
        if (settings.LogRetention is { } retention)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(retention.Rules);
                if (retention.Rules.Any(rule => rule is null))
                {
                    throw new ArgumentException("ログ保持規則にnullは指定できません。", nameof(settings));
                }
                _ = new LogPolicy(new LogOptions("validation", Path.GetTempPath(), retention.Rules, RollingTime: retention.RollingTime, TimeZoneId: retention.TimeZoneId));
                if (retention.Rules.Count != 5 || retention.Rules.Select(rule => rule.Level).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
                {
                    issues.Add(new("cluster.logRetention.levels", "全ログレベルの保持方針を重複なく指定してください。"));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                issues.Add(new("cluster.logRetention.invalid", "保持期間、ローリング時刻またはタイムゾーンが不正です。"));
            }
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            issues.Add(new("cluster.timeZone.invalid", "解決可能な共通timezone識別子が必要です。"));
        }
        if (!Enum.IsDefined(settings.FileConflictPolicy) || !Enum.IsDefined(settings.FileInternalFailurePolicy))
        {
            issues.Add(new("cluster.filePolicy.invalid", "定義されたCluster共通ファイル方針が必要です。"));
        }
        if (!Enum.IsDefined(settings.SafetyConfirmation))
        {
            issues.Add(new("cluster.safetyConfirmation.invalid", "定義された一時操作前確認方針が必要です。"));
        }
        if (settings.HostGroups is null || settings.HostGroups.Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Value is null || group.Value.Any(string.IsNullOrWhiteSpace) || group.Value.Distinct(StringComparer.Ordinal).Count() != group.Value.Count))
        {
            issues.Add(new("cluster.hostGroups.invalid", "Group名と重複のないHost識別子集合が必要です。"));
        }
        return issues;
    }

    /// <summary>未開始または不明の各段階を成功に置換しません。</summary>
    /// <returns>共通段階結果です。</returns>
    internal static ClusterOperationalSaveResult Unstarted(Guid id, EOutcome outcome, string reason)
    {
        ConfigurationPhaseResult value = new(outcome, reason);
        ConfigurationPhaseResult none = new(EOutcome.NotExecuted);
        return new(id, null, [], value, value, none, none);
    }
}
