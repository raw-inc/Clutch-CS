using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>共通設定は既知の項目とGroup単位で非競合変更を比較します。</summary>
internal static class ConflictOperationalMerge
{
    /// <summary>同じ項目への異なる変更がなければ、元版に各側の変更を反映します。</summary>
    /// <returns>統合可能かと同一の内容版です。</returns>
    internal static (bool Safe, NodeValue? Value) Create(DurableEntry original, IReadOnlyList<DurableEntry> candidates)
    {
        ClusterOperationalSnapshot baseline = ConflictCandidateValidation.Operational(original.Scope, original.Value!)!;
        ClusterOperationalSnapshot[] versions = candidates.Select(entry => ConflictCandidateValidation.Operational(entry.Scope, entry.Value!)!).ToArray();
        List<string> unresolved = [];
        ClusterOperationalSettings settings = baseline.Settings with
        {
            TimeZoneId = Select(baseline.Settings.TimeZoneId, versions.Select(version => version.Settings.TimeZoneId), "TimeZoneId", unresolved),
            FileConflictPolicy = Select(baseline.Settings.FileConflictPolicy, versions.Select(version => version.Settings.FileConflictPolicy), "FileConflictPolicy", unresolved),
            FileInternalFailurePolicy = Select(baseline.Settings.FileInternalFailurePolicy, versions.Select(version => version.Settings.FileInternalFailurePolicy), "FileInternalFailurePolicy", unresolved),
            SafetyConfirmation = Select(baseline.Settings.SafetyConfirmation, versions.Select(version => version.Settings.SafetyConfirmation), "SafetyConfirmation", unresolved),
            LiveWorkflowUpdates = Select(baseline.Settings.LiveWorkflowUpdates, versions.Select(version => version.Settings.LiveWorkflowUpdates), "LiveWorkflowUpdates", unresolved),
            LogRetention = Select(baseline.Settings.LogRetention, versions.Select(version => version.Settings.LogRetention), "LogRetention", unresolved),
            DataRetention = Select(baseline.Settings.DataRetention, versions.Select(version => version.Settings.DataRetention), "DataRetention", unresolved),
        };
        Dictionary<string, IReadOnlyList<string>> groups = new(StringComparer.Ordinal);
        foreach (string key in baseline.Settings.HostGroups.Keys.Concat(versions.SelectMany(version => version.Settings.HostGroups.Keys)).Distinct(StringComparer.Ordinal))
        {
            Group baselineGroup = ReadGroup(baseline.Settings, key);
            Group group = Select(baselineGroup, versions.Select(version => ReadGroup(version.Settings, key)), "HostGroups/" + key, unresolved);
            if (group.Exists)
            {
                groups[key] = group.Hosts;
            }
        }
        settings = settings with { HostGroups = groups };
        ClusterOperationalSnapshot merged = new(settings, ContractJson.Hash(settings), versions.Max(version => version.SavedAt), candidates.Select(entry => entry.Version).Order(StringComparer.Ordinal).ToArray(), unresolved);
        string activation = "cluster.operational.activation";
        if (string.Equals(original.Scope, activation, StringComparison.Ordinal))
        {
            return (unresolved.Count == 0, NodeValue.FromObject(merged));
        }
        ClusterConfigurationStored stored = original.Value!.Json.Deserialize<ClusterConfigurationStored>()!;
        EActivationPolicy policy = Select(stored.ActivationPolicy, candidates.Select(entry => entry.Value!.Json.Deserialize<ClusterConfigurationStored>()!.ActivationPolicy), "ActivationPolicy", unresolved);
        return (unresolved.Count == 0, NodeValue.FromObject(new ClusterConfigurationStored(merged, policy)));
    }

    /// <summary>空Groupと削除を同じ状態に丸めません。</summary>
    /// <returns>存在情報を含む所属です。</returns>
    private static Group ReadGroup(ClusterOperationalSettings settings, string name)
    {
        bool exists = settings.HostGroups.TryGetValue(name, out IReadOnlyList<string>? members);
        return new(exists, members?.Order(StringComparer.Ordinal).ToArray() ?? []);
    }

    /// <summary>元値と異なる候補が一つの意味に限られる場合だけ採用します。</summary>
    /// <returns>非競合値、競合時は元値です。</returns>
    private static T Select<T>(T original, IEnumerable<T> candidates, string field, List<string> unresolved)
    {
        string baseline = ContractJson.Hash(original);
        T[] changed = candidates.Where(candidate => !string.Equals(ContractJson.Hash(candidate), baseline, StringComparison.Ordinal)).DistinctBy(candidate => ContractJson.Hash(candidate), StringComparer.Ordinal).ToArray();
        if (1 < changed.Length)
        {
            unresolved.Add(field);
            return original;
        }
        if (changed.Length == 1)
        {
            return changed[0];
        }
        return original;
    }

    /// <summary>Group削除と空所属を区別する比較値です。</summary>
    private sealed record Group(bool Exists, IReadOnlyList<string> Hosts);
}
