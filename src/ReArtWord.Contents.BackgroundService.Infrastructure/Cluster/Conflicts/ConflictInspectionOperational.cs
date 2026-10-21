using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>共通設定分岐の値を型検証し、変わった項目だけを列挙します。</summary>
internal static class ConflictInspectionOperational
{
    private static readonly HashSet<string> scopes = new(["cluster.operational", "cluster.operational.activation", "cluster.operational.history"], StringComparer.Ordinal);

    /// <summary>共通設定の保存用途かを確認します。</summary>
    /// <returns>共通設定の用途か。</returns>
    internal static bool IsScope(string scope) => scopes.Contains(scope);

    /// <summary>保存形式の違いを吸収して設定値だけを比較します。</summary>
    /// <returns>異なる共通設定項目です。</returns>
    internal static IReadOnlyList<string> Differences(IReadOnlyList<DurableEntry> candidates)
    {
        ClusterOperationalSettings?[] settings = candidates.Select(Read).ToArray();
        List<string> fields = [];
        if (Different(settings.Select(static item => item?.TimeZoneId)))
        {
            fields.Add("TimeZoneId");
        }
        if (Different(settings.Select(static item => item?.FileConflictPolicy)))
        {
            fields.Add("FileConflictPolicy");
        }
        if (Different(settings.Select(static item => item?.FileInternalFailurePolicy)))
        {
            fields.Add("FileInternalFailurePolicy");
        }
        if (Different(settings.Select(static item => item?.LogRetention)))
        {
            fields.Add("LogRetention");
        }
        if (Different(settings.Select(static item => item?.DataRetention)))
        {
            fields.Add("DataRetention");
        }
        if (Different(settings.Select(static item => item?.LiveWorkflowUpdates)))
        {
            fields.Add("LiveWorkflowUpdates");
        }
        foreach (string group in settings.SelectMany(static item => item?.HostGroups.Keys ?? []).Distinct(StringComparer.Ordinal))
        {
            if (Different(settings.Select(item => item?.HostGroups.GetValueOrDefault(group)?.Order(StringComparer.Ordinal).ToArray())))
            {
                fields.Add("HostGroups/" + group);
            }
        }
        return fields;
    }

    /// <summary>型・既定列挙値・revisionが検証できない候補を拒否します。</summary>
    /// <returns>比較する設定、または削除です。</returns>
    private static ClusterOperationalSettings? Read(DurableEntry entry)
    {
        if (entry.Value is null)
        {
            return null;
        }
        string activation = "cluster.operational.activation";
        ClusterOperationalSnapshot? snapshot;
        if (string.Equals(entry.Scope, activation, StringComparison.Ordinal))
        {
            snapshot = entry.Value.Json.Deserialize<ClusterOperationalSnapshot>(ContractJson.Options);
        }
        else
        {
            snapshot = entry.Value.Json.Deserialize<ClusterConfigurationStored>(ContractJson.Options)?.Snapshot;
        }
        if (snapshot?.Settings is null || ClusterConfigurationValidation.Validate(snapshot.Settings).Count != 0
            || !string.Equals(snapshot.Revision, ContractJson.Hash(snapshot.Settings), StringComparison.Ordinal))
        {
            throw new JsonException("共通設定の候補を検証できません。");
        }
        return snapshot.Settings;
    }

    /// <summary>項目の並びによらず同じ契約値かを比較します。</summary>
    /// <returns>異なる値を含むか。</returns>
    private static bool Different<T>(IEnumerable<T> values) => values.Select(ContractJson.Hash).Distinct(StringComparer.Ordinal).Skip(1).Any();
}
