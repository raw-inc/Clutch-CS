using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Snapshots;

/// <summary>スナップショットに収録されていない範囲への暗黙拡大を防ぎます。</summary>
internal static class SnapshotSelectionRules
{
    /// <summary>同じ対象の全体または上位の収録範囲だけを復元元に許可します。</summary>
    /// <returns>明示範囲をスナップショットが含むか。</returns>
    internal static bool Covers(SnapshotListSummary record, SnapshotTarget target)
    {
        if (record.DetailReason is not null || record.ScopeOmitted || record.Id is null || record.Kind != target.Kind || !string.Equals(record.TargetId, target.Id, StringComparison.Ordinal) || !string.Equals(record.PeerId, target.PeerId, StringComparison.Ordinal))
        {
            return false;
        }
        if (record.NodeIds is null)
        {
            return true;
        }
        return target.NodeIds is not null && target.NodeIds.All(id => record.NodeIds.Contains(id, StringComparer.Ordinal));
    }
}
