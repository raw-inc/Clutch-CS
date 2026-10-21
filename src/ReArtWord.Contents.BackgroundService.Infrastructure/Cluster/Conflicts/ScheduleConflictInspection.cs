using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>時間指定の進行記録を設定値と区別し、巻戻しによる再実行を防ぎます。</summary>
internal static class ScheduleConflictInspection
{
    /// <summary>共有される時間指定記録の保存領域を判定します。</summary>
    /// <returns>予定位置・保持入力・その索引のいずれかか。</returns>
    internal static bool IsScope(string scope) => scope.StartsWith(ScheduleCursorStore.ScopePrefix, StringComparison.Ordinal)
        || string.Equals(scope, ScheduledInputStore.InputScope, StringComparison.Ordinal) || string.Equals(scope, ScheduledInputStore.IndexScope, StringComparison.Ordinal);

    /// <summary>同じ内容の分岐だけを解消可能とし、異なる進行位置の選び直しは拒否します。</summary>
    /// <returns>対象ワークフローと解消可否を含む競合。</returns>
    internal static StateConflict Describe(IReadOnlyList<DurableEntry> candidates)
    {
        DurableEntry first = candidates[0];
        HashSet<string> workflows = new(StringComparer.Ordinal);
        bool valid = true;
        try
        {
            foreach (DurableEntry candidate in candidates)
            {
                if (candidate.Scope.StartsWith(ScheduleCursorStore.ScopePrefix, StringComparison.Ordinal))
                {
                    workflows.Add(candidate.Scope[ScheduleCursorStore.ScopePrefix.Length..]);
                    ScheduleProgressRecord? progress = candidate.Value?.Json.Deserialize<ScheduleProgressRecord>(ContractJson.Options);
                    valid &= progress?.Cursor is { } cursor && cursor.Count is >= 0 && string.Equals(cursor.Key, first.Key, StringComparison.Ordinal);
                }
                else
                {
                    if (string.Equals(candidate.Scope, ScheduledInputStore.InputScope, StringComparison.Ordinal))
                    {
                        ScheduledInputRecord? input = candidate.Value?.Json.Deserialize<ScheduledInputRecord>(ContractJson.Options);
                        valid &= input is not null && string.Equals(input.Id.ToString("N"), first.Key, StringComparison.Ordinal);
                        if (input is not null)
                        {
                            workflows.Add(input.WorkflowId);
                        }
                    }
                    else
                    {
                        ScheduledInputIndex? index = candidate.Value?.Json.Deserialize<ScheduledInputIndex>(ContractJson.Options);
                        valid &= index is not null && index.Entries is not null && string.Equals(first.Key, ScheduledInputStore.IndexKey, StringComparison.Ordinal);
                    }
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            valid = false;
        }
        bool equal = candidates.Select(item => ContractJson.Hash(item.Value)).Distinct(StringComparer.Ordinal).Count() == 1;
        StateConflictCandidate[] values = candidates.Select(item => new StateConflictCandidate(item.Version, item.Value?.Copy())).ToArray();
        return new(ContractJson.Hash(new { first.Scope, first.Key }), first.Scope, first.Key, values, workflows.ToArray(), [], false, valid && equal);
    }

    /// <summary>時間指定の競合で、関係のない通信・監視Sourceを停止しません。</summary>
    /// <returns>この時間指定ノードが競合範囲に含まれるか。</returns>
    internal static bool DependsOn(StateConflict conflict, string workflowId, NodeDefinition node)
    {
        string timer = "scheduling.timer";
        if (!string.Equals(node.Type, timer, StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(conflict.Scope, ScheduledInputStore.IndexScope, StringComparison.Ordinal))
        {
            return true;
        }
        if (conflict.WorkflowIds.Count != 0 && !conflict.WorkflowIds.Contains(workflowId, StringComparer.Ordinal))
        {
            return false;
        }
        return !conflict.Scope.StartsWith(ScheduleCursorStore.ScopePrefix, StringComparison.Ordinal) || conflict.Key.StartsWith(node.Id + ":", StringComparison.Ordinal);
    }

    /// <summary>確定内容が同じ候補の選択だけを許可し、編集による巻戻しを拒否します。</summary>
    /// <returns>候補が保持記録と異なる場合の問題。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(StateConflict conflict, NodeValue? value)
    {
        string hash = ContractJson.Hash(value);
        if (value is null || conflict.Candidates.Any(candidate => !string.Equals(ContractJson.Hash(candidate.Value), hash, StringComparison.Ordinal)))
        {
            return [new("conflict.scheduleRewind", "時間指定の進行位置・配送許可・取消記録は、候補の編集や削除では変更できません。")];
        }
        return [];
    }
}
