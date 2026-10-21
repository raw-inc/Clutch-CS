using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>反復内の配置に到着した公開入力だけを、内部入口の開始条件へ引き継ぎます。</summary>
internal static class RecipeIterationInputs
{
    /// <summary>配線されていない内部入口も同じ反復へ所属させ、未選択・未供給は開始しません。</summary>
    /// <returns>入力の不足と、今回の反復での受信有無です。</returns>
    internal static (bool Missing, bool Received) Check(CompiledWorkflow workflow, string node, IEnumerable<ExecutionFrame> parents)
    {
        if (!workflow.RecipeInputs.TryGetValue(node, out IReadOnlyList<string>? inputs))
        {
            return (false, false);
        }
        Dictionary<string, ExecutionFrame> frames = parents.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal);
        string value = "value";
        if (inputs.Any(input => !frames.TryGetValue(input, out ExecutionFrame? frame) || frame.Outcome != EOutcome.Succeeded
            || !frame.Targets.Any(target => target.Result.Outputs.ContainsKey(value))))
        {
            return (true, false);
        }
        return (false, inputs.Any(input => !frames[input].ConfigurationOnly && !frames[input].RetainedValue));
    }

    /// <summary>要求時評価も依存元が所属する最内周の反復へ固定します。</summary>
    /// <returns>現在の要素の識別子、反復外なら空です。</returns>
    internal static async Task<Guid> ScopeAsync(CompiledWorkflow workflow, string node, Func<string, Task<ExecutionFrame>?> read, CancellationToken cancellationToken = default)
    {
        IterationRegion? region = workflow.Iterations?.Regions.Where(region => region.Members.Contains(node)).MinBy(region => region.Members.Count);
        if (region is not null && read(region.Start.Id) is { } pending)
        {
            ExecutionFrame frame = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return frame.SignalId;
        }
        return Guid.Empty;
    }
}
