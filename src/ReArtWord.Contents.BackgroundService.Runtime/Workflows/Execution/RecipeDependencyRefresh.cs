using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>再利用定義の変更を、依存する配置先の次の入力へ反映します。</summary>
internal static class RecipeDependencyRefresh
{
    /// <summary>先に変更元を適用し、依存順で各計画を一度だけ再構築します。</summary>
    /// <returns>元の待受との設定差を確認する対象です。</returns>
    internal static IReadOnlySet<string> Apply(CompiledWorkflow workflow, ConcurrentDictionary<string, CompiledWorkflow> effective, NodeRegistry registry)
    {
        effective[workflow.Definition.Id] = workflow;
        return Refresh(workflow.Definition.Id, effective, registry);
    }

    /// <summary>削除や変更に依存する計画だけを更新し、不正な依存を実行させません。</summary>
    /// <returns>関係するワークフロー識別子です。</returns>
    internal static IReadOnlySet<string> Refresh(string changed, ConcurrentDictionary<string, CompiledWorkflow> effective, NodeRegistry registry)
    {
        HashSet<string> affected = new(StringComparer.Ordinal) { changed };
        Dictionary<string, string[]> dependencies = effective.ToDictionary(pair => pair.Key, pair => pair.Value.Definition.Nodes
            .Where(node => SubworkflowInterface.Captured(node) is null).Select(node => SubworkflowInterface.Target(node.Type)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        bool added;
        do
        {
            added = false;
            foreach ((string id, string[] targets) in dependencies)
            {
                if (targets.Any(affected.Contains))
                {
                    added |= affected.Add(id);
                }
            }
        } while (added);
        HashSet<string> remaining = affected.Where(id => !string.Equals(id, changed, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        WorkflowCompiler compiler = new(registry);
        while (remaining.Count != 0)
        {
            string[] ready = remaining.Where(id => dependencies[id].All(target => !remaining.Contains(target))).ToArray();
            if (ready.Length == 0)
            {
                foreach (string id in remaining)
                {
                    effective[id] = effective[id] with { DependencyIssues = [new("subworkflow.recursion", "Recursive recipe references cannot run.")] };
                }
                break;
            }
            foreach (string id in ready)
            {
                CompiledWorkflow current = effective[id];
                CompilationResult result = compiler.Compile(current.Definition);
                effective[id] = result.Workflow ?? current with { DependencyIssues = result.Issues };
                remaining.Remove(id);
            }
        }
        return affected;
    }
}
