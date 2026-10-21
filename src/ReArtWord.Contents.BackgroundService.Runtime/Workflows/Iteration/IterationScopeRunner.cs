using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>一階層の依存待ちを所有し、制御信号の後は未開始の処理だけを省略します。</summary>
internal sealed class IterationScopeRunner(CompiledWorkflow workflow, IterationNodeExecution execute,
    Func<IterationRegion, ExecutionFrame, Dictionary<string, ExecutionFrame>, Func<string, Task<ExecutionFrame>?>, IterationControl?, CancellationToken, Task<Dictionary<string, ExecutionFrame>>> iterate,
    Func<ExecutionFrame, Func<string, Task<ExecutionFrame>?>, Task> publish)
{
    /// <summary>開始した全タスクの結果を回収し、次の要素へ未完了処理を持ち越しません。</summary>
    /// <returns>同じ階層で確定したノード結果です。</returns>
    internal async Task<Dictionary<string, ExecutionFrame>> RunAsync(HashSet<string> members, string? activeStart,
        Dictionary<string, ExecutionFrame> inherited, HashSet<string> entries, IReadOnlyDictionary<string, NodeValue>? inputs,
        IterationControl? control = null, Func<string, Task<ExecutionFrame>?>? outerRead = null, ExecutionFrame? emission = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> owners = workflow.Iterations!.Owners(members, activeStart);
        Dictionary<string, IterationRegion> children = workflow.Iterations.Children(members, activeStart).ToDictionary(region => region.Start.Id, StringComparer.Ordinal);
        Dictionary<string, Lazy<Task<Dictionary<string, ExecutionFrame>>>> tasks = new(StringComparer.Ordinal);

        Task<ExecutionFrame>? Read(string id)
        {
            if (inherited.TryGetValue(id, out ExecutionFrame? frame))
            {
                return Task.FromResult(frame);
            }
            if (owners.TryGetValue(id, out string? owner))
            {
                return ScopedSignalMerge.FrameAsync(tasks[owner].Value, id, cancellationToken);
            }
            return outerRead?.Invoke(id);
        }

        async Task<Dictionary<string, ExecutionFrame>> StartAsync(string unit)
        {
            // 読み取り関数を公開する前に全単位を登録し、子実行からの要求で再入しても同じTaskを共有します。
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if (inherited.TryGetValue(unit, out ExecutionFrame? seed))
            {
                control?.Observe(seed);
                return new(StringComparer.Ordinal) { [unit] = seed };
            }
            string[] ids = owners.Where(pair => string.Equals(pair.Value, unit, StringComparison.Ordinal)).Select(pair => pair.Key).ToArray();
            string[] dependencies = ids.SelectMany(workflow.Parents).Where(owners.ContainsKey).Select(id => owners[id])
                .Where(id => !string.Equals(id, unit, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            Task<Dictionary<string, ExecutionFrame>>[] parents = dependencies.Select(id => tasks[id].Value).ToArray();
            Task<ExecutionFrame>[]? firstInputs = null;
            if (SignalMergePorts.IsScopedFirst(workflow.FindNode(unit)!))
            {
                firstInputs = workflow.Incoming[unit].Select(wire => wire.FromNode).Distinct(StringComparer.Ordinal)
                    .Select(id =>
                    {
                        if (inherited.TryGetValue(id, out ExecutionFrame? frame))
                        {
                            return Task.FromResult(frame);
                        }
                        return ScopedSignalMerge.FrameAsync(tasks[owners[id]].Value, id, cancellationToken);
                    }).ToArray();
            }
            Guid iterationScope = Guid.Empty;
            if (activeStart is not null)
            {
                iterationScope = inherited[activeStart].SignalId;
            }
            return await ExecuteUnitAsync(unit, children.GetValueOrDefault(unit), parents, inherited,
                entries, inputs, iterationScope, firstInputs, Read, control, cancellationToken).ConfigureAwait(false);
        }

        foreach (string unit in owners.Values.Distinct(StringComparer.Ordinal))
        {
            tasks.Add(unit, new(() => StartAsync(unit), LazyThreadSafetyMode.ExecutionAndPublication));
        }
        if (emission is not null)
        {
            await publish(emission, Read).ConfigureAwait(false);
        }
        Task<Dictionary<string, ExecutionFrame>>[] all = tasks.Values.Select(task => task.Value).ToArray();
        Dictionary<string, ExecutionFrame>[] results = await Task.WhenAll(all).ConfigureAwait(false);
        return results.SelectMany(result => result).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>親の完了かEndの制御信号を待ち、今回の範囲に属する処理だけを開始します。</summary>
    /// <returns>その単位が所有する確定結果です。</returns>
    private async Task<Dictionary<string, ExecutionFrame>> ExecuteUnitAsync(string id, IterationRegion? region,
        Task<Dictionary<string, ExecutionFrame>>[] parents, Dictionary<string, ExecutionFrame> inherited, HashSet<string> entries,
        IReadOnlyDictionary<string, NodeValue>? inputs, Guid iterationScope, Task<ExecutionFrame>[]? firstInputs, Func<string, Task<ExecutionFrame>?> read,
        IterationControl? control, CancellationToken cancellationToken = default)
    {
        bool end = string.Equals(control?.End?.Id, id, StringComparison.Ordinal);
        if (control?.IsClosed == true && (!end || control.ParentClosed))
        {
            return SkippedUnit(id, region);
        }
        NodeDefinition node = workflow.FindNode(id)!;
        if (EventMergePorts.IsBoundary(node))
        {
            Task<ExecutionFrame>[] arrivals = workflow.Parents(id).Select(parent => read(parent)!).ToArray();
            ExecutionFrame boundary = await execute(node, arrivals, entries.Contains(id), inputs, read, iterationScope, () => control?.IsClosed == true).ConfigureAwait(false);
            control?.Observe(boundary);
            return new(StringComparer.Ordinal) { [id] = boundary };
        }
        Dictionary<string, ExecutionFrame> available;
        ExecutionFrame[] upstream;
        if (end)
        {
            available = await control!.EndInputsAsync(parents, inherited, cancellationToken).ConfigureAwait(false);
            upstream = workflow.Parents(id).Select(parent => available[parent]).ToArray();
        }
        else
        {
            available = new(inherited, StringComparer.Ordinal);
            if (firstInputs is not null)
            {
                upstream = await ScopedSignalMerge.FirstAsync(workflow, node, firstInputs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Dictionary<string, ExecutionFrame>[] completed = await Task.WhenAll(parents).ConfigureAwait(false);
                foreach ((string key, ExecutionFrame frame) in completed.SelectMany(result => result))
                {
                    available[key] = frame;
                }
                upstream = workflow.Parents(id).Select(parent => available[parent]).ToArray();
            }
        }
        if (control?.IsClosed == true && (!end || control.ParentClosed))
        {
            return SkippedUnit(id, region);
        }
        ExecutionFrame first = await execute(node, upstream.Select(Task.FromResult).ToArray(), entries.Contains(id), inputs, read, iterationScope, () => control?.IsClosed == true).ConfigureAwait(false);
        if (region is null)
        {
            control?.Observe(first);
            return new(StringComparer.Ordinal) { [id] = first };
        }
        Dictionary<string, ExecutionFrame> finished = await iterate(region, first, available, read, control, cancellationToken).ConfigureAwait(false);
        foreach (ExecutionFrame frame in finished.Values)
        {
            control?.Observe(frame);
        }
        return finished;
    }

    /// <summary>入れ子も含め、制御後に未開始だったノードを実行成功と区別して残します。</summary>
    /// <returns>この単位に属する未実行結果です。</returns>
    private static Dictionary<string, ExecutionFrame> SkippedUnit(string id, IterationRegion? region)
    {
        IEnumerable<string> nodes = region?.Members ?? [id];
        return nodes.ToDictionary(key => key, key => new ExecutionFrame(key, EOutcome.NotExecuted,
            [new(string.Empty, NodeResult.Failure("iteration.controlled", "The iteration was explicitly continued or broken before this node started.", EOutcome.NotExecuted))]), StringComparer.Ordinal);
    }
}
