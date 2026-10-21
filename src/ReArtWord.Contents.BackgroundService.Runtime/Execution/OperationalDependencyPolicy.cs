using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Execution;

/// <summary>共通設定の未解消項目に依存する処理だけを開始前に隔離します。</summary>
public static class OperationalDependencyPolicy
{
    /// <summary>一件に保持された共通設定の未解消項目を確認します。</summary>
    /// <returns>開始不可理由、または非依存時のnullです。</returns>
    public static string? GetProblem(ClusterOperationalSnapshot? snapshot, NodeDefinition node, NodeDescriptor descriptor, bool fileFootprint = false)
    {
        if (DependsOn(snapshot?.UnresolvedFields ?? [], node, descriptor, fileFootprint))
        {
            return "Conflict.OperationalDependency";
        }
        return null;
    }

    /// <summary>条件ノードの時刻窓だけが共通timezoneを参照します。</summary>
    /// <returns>時刻窓条件か。</returns>
    private static bool UsesTimeWindow(NodeDefinition node)
    {
        string type = "control.condition";
        string property = "operation";
        string operation = "timeWindow";
        return string.Equals(node.Type, type, StringComparison.Ordinal) && node.Settings.ValueKind == JsonValueKind.Object
            && node.Settings.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), operation, StringComparison.Ordinal);
    }

    /// <summary>ファイル方針・参照Group・日時計算の差分だけを適用します。</summary>
    /// <returns>当該設定項目に依存するか。</returns>
    public static bool DependsOn(IReadOnlyList<string> fields, NodeDefinition node, NodeDescriptor descriptor, bool fileFootprint)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(descriptor);
        string groupPrefix = "HostGroups/";
        string zone = "TimeZoneId";
        string scheduling = "Scheduling";
        string conflictPolicy = "FileConflictPolicy";
        string failurePolicy = "FileInternalFailurePolicy";
        foreach (string field in fields)
        {
            if (field.StartsWith(groupPrefix, StringComparison.Ordinal) && node.Target?.Groups?.Contains(field[groupPrefix.Length..], StringComparer.Ordinal) == true)
            {
                return true;
            }
            if ((string.Equals(field, conflictPolicy, StringComparison.Ordinal) || string.Equals(field, failurePolicy, StringComparison.Ordinal)) && fileFootprint)
            {
                return true;
            }
            if (string.Equals(field, zone, StringComparison.Ordinal) && (string.Equals(descriptor.Category, scheduling, StringComparison.Ordinal) || UsesTimeWindow(node)))
            {
                return true;
            }
        }
        return false;
    }
}
