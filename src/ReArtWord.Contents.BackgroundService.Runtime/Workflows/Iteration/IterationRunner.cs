using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>親の固定文脈を共有し、ボディの完了を待つ有限反復を実行します。</summary>
internal sealed class IterationRunner
{
    private readonly CompiledWorkflow workflow;
    private readonly IterationScopeRunner scopes;
    private readonly Func<bool> cancelled;
    private readonly TimeProvider clock;
    private readonly IterationBudget budget = new();
    private readonly Func<ExecutionFrame, Func<string, Task<ExecutionFrame>?>, Task> publish;
    private readonly NodeValueObservations observations;
    private readonly Guid causationId;
    private readonly string ownerPeer;
    private readonly Action<Guid>? scopeCompleted;
    private readonly ConcurrencyRegionRunner concurrency;

    /// <summary>通常ノードの配送境界をそのまま使用します。</summary>
    internal IterationRunner(CompiledWorkflow workflow, IterationNodeExecution execute, Func<bool> cancelled, TimeProvider clock,
        Func<ExecutionFrame, Func<string, Task<ExecutionFrame>?>, Task> publish, NodeValueObservations observations, Guid causationId, string ownerPeer,
        WorkflowConcurrencyExecution concurrency, Func<Guid, Task<EOutcome>> completeScope, Action<Guid, Guid> inheritProgress, Action<Guid>? scopeCompleted = null)
    {
        this.workflow = workflow;
        scopes = new(workflow, execute, RunRegionAsync, publish);
        this.concurrency = new(concurrency, scopes, completeScope, inheritProgress, cancelled);
        this.cancelled = cancelled;
        this.clock = clock;
        this.publish = publish;
        this.observations = observations;
        this.causationId = causationId;
        this.ownerPeer = ownerPeer;
        this.scopeCompleted = scopeCompleted;
    }

    /// <summary>親グラフの各反復を一つの完了単位として開始します。</summary>
    /// <returns>通常ノードと反復範囲の実行結果です。</returns>
    internal async Task<ExecutionFrame[]> RunAsync(HashSet<string> entries, IReadOnlyDictionary<string, NodeValue>? inputs, ExecutionFrame? seed, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ExecutionFrame> inherited = new(StringComparer.Ordinal);
        if (seed is not null)
        {
            inherited.Add(seed.NodeId, seed);
        }
        Dictionary<string, ExecutionFrame> frames = await scopes.RunAsync(workflow.OrderedNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal), null, inherited, entries, inputs, emission: seed, cancellationToken: cancellationToken).ConfigureAwait(false);
        return workflow.OrderedNodes.Select(node => frames[node.Id]).ToArray();
    }

    /// <summary>反復範囲の開始から全処理の終了までを観測し、集約結果を一回だけ公開します。</summary>
    /// <returns>開始済みのボディを回収した反復結果です。</returns>
    private async Task<Dictionary<string, ExecutionFrame>> RunRegionAsync(IterationRegion region, ExecutionFrame first,
        Dictionary<string, ExecutionFrame> available, Func<string, Task<ExecutionFrame>?> read, IterationControl? parentControl, CancellationToken cancellationToken = default)
    {
        NodeValueObservation[] running = new[] { region.Start.Id, region.End?.Id }.OfType<string>()
            .Select(nodeId => new NodeValueObservation(Guid.NewGuid(), causationId, workflow.Definition.Id, workflow.Revision, nodeId, ownerPeer, clock.GetUtcNow())).ToArray();
        foreach (NodeValueObservation value in running)
        {
            observations.Record(value);
        }
        Dictionary<string, ExecutionFrame> finished;
        EOutcome outcome = EOutcome.Unknown;
        try
        {
            if (ConcurrencyNodeTypes.IsStart(region.Start.Type))
            {
                IterationRegion? parent = workflow.Iterations!.Regions.Where(candidate => region.Members.IsProperSubsetOf(candidate.Members)).MinBy(candidate => candidate.Members.Count);
                Guid inheritedScope = parent is null ? Guid.Empty
                                                     : available.GetValueOrDefault(parent.Start.Id)?.SignalId ?? Guid.Empty;
                finished = await concurrency.RunAsync(region, first, available, read, parentControl, inheritedScope, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                finished = await RunIterationAsync(region, first, available, read, parentControl, cancellationToken).ConfigureAwait(false);
            }
            outcome = finished[region.Start.Id].Outcome;
        }
        finally
        {
            foreach (NodeValueObservation value in running)
            {
                observations.Record(value with { CompletedAt = clock.GetUtcNow(), Outcome = outcome });
            }
        }
        Task<ExecutionFrame>? Read(string id) => finished.TryGetValue(id, out ExecutionFrame? frame) ? Task.FromResult(frame)
                                                                                                     : read(id);
        await publish(finished[region.Start.Id], Read).ConfigureAwait(false);
        if (region.End is not null)
        {
            await publish(finished[region.End.Id] with { SignalId = Guid.NewGuid() }, Read).ConfigureAwait(false);
        }
        return finished;
    }

    /// <summary>固定数のworkerで要素を取り出し、失敗後は未開始要素を開始しません。</summary>
    /// <returns>入力順の集約とボディの実際の成否です。</returns>
    private async Task<Dictionary<string, ExecutionFrame>> RunIterationAsync(IterationRegion region, ExecutionFrame first, Dictionary<string, ExecutionFrame> inherited,
        Func<string, Task<ExecutionFrame>?> read, IterationControl? parentControl, CancellationToken cancellationToken = default)
    {
        if (first.Outcome != EOutcome.Succeeded || first.Targets.Count != 1 || !first.Targets[0].Result.Outputs.TryGetValue(IterationNodeTypes.LoopPort, out NodeValue? array) || array.Kind != EValueKind.Json || array.Json.ValueKind != JsonValueKind.Array)
        {
            if (first.Outcome == EOutcome.Succeeded)
            {
                first = first with { Outcome = EOutcome.Failed, Targets = [new(string.Empty, NodeResult.Failure("iteration.inputResult", "The start did not return one confirmed array; no body was started."))] };
            }
            return Unstarted(region, first);
        }
        int count = array.Json.GetArrayLength();
        int concurrency = 1;
        TimeSpan interval = TimeSpan.Zero;
        if (string.Equals(region.Start.Type, IterationNodeTypes.ParallelForEach, StringComparison.Ordinal))
        {
            NodeSettings settings = new(region.Start.Settings);
            concurrency = settings.ReadInt32("maxConcurrency", 4);
            interval = TimeSpan.FromMilliseconds(settings.ReadInt32("startIntervalMs", 0));
        }
        using SemaphoreSlim? admissions = TimeSpan.Zero < interval ? new(1, 1)
                                                                   : null;
        IterationStartSlots? spacing = null;
        if (admissions is not null)
        {
            spacing = new(clock, interval);
        }
        IterationExecutionResult?[] details = new IterationExecutionResult?[count];
        IterationResults? results = null;
        if (region.End is not null)
        {
            results = new(region.End, workflow.Definition, count);
        }
        bool breaking = false;
        Dictionary<string, ExecutionFrame> summaries = new(StringComparer.Ordinal);
        Lock gate = new();
        int next = 0;
        int started = 0;
        EOutcome outcome = EOutcome.Succeeded;
        string? problem = null;

        async ValueTask<int> NextAsync()
        {
            if (admissions is not null)
            {
                await admissions.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                TimeSpan? scheduled = null;
                while (Volatile.Read(ref next) < count)
                {
                    TimeSpan remaining = TimeSpan.Zero;
                    using (gate.EnterScope())
                    {
                        if (next == count || outcome != EOutcome.Succeeded || Volatile.Read(ref breaking) || parentControl?.IsClosed == true)
                        {
                            return -1;
                        }
                        if (cancelled() || cancellationToken.IsCancellationRequested)
                        {
                            outcome = EOutcome.Cancelled;
                            problem = "iteration.cancelled";
                            return -1;
                        }
                        remaining = spacing?.Remaining(ref scheduled) ?? TimeSpan.Zero;
                        if (remaining <= TimeSpan.Zero)
                        {
                            if (!budget.TryBegin())
                            {
                                outcome = EOutcome.Failed;
                                problem = "iteration.limit";
                                return -1;
                            }
                            spacing?.Started();
                            started++;
                            return next++;
                        }
                    }

                    // 開始待ちは一つだけに絞り、外側のbreakや安全停止も有限時間で再確認します。
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, 100)), clock, cancellationToken).ConfigureAwait(false);
                }
                return -1;
            }
            finally
            {
                admissions?.Release();
            }
        }

        async Task WorkerAsync()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            for (int attempt = 0; attempt < count; attempt++)
            {
                int index;
                try
                {
                    index = await NextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    using (gate.EnterScope())
                    {
                        outcome = IterationOutcomes.Combine(outcome, EOutcome.Cancelled);
                        if (outcome == EOutcome.Cancelled)
                        {
                            problem = "iteration.cancelled";
                        }
                    }
                    return;
                }
                if (index < 0)
                {
                    return;
                }
                DateTimeOffset begin = clock.GetUtcNow();
                Dictionary<string, ExecutionFrame> seed = new(inherited, StringComparer.Ordinal) { [region.Start.Id] = Element(first, JsonPointer.Select(array, "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), index, count) };
                try
                {
                    IterationControl control = new(workflow, region.End, () => Volatile.Write(ref breaking, true), parentControl);
                    Dictionary<string, ExecutionFrame> body = await scopes.RunAsync(region.Members, region.Start.Id, seed, [], null, control, read, seed[region.Start.Id], cancellationToken).ConfigureAwait(false);
                    ExecutionFrame[] bodyFrames = body.Values.Where(frame => !string.Equals(frame.NodeId, region.Start.Id, StringComparison.Ordinal)).ToArray();
                    bool cancellationRequested = cancelled() || cancellationToken.IsCancellationRequested;
                    EOutcome actual = EOutcome.Succeeded;
                    if (bodyFrames.Length != 0)
                    {
                        actual = IterationOutcomes.BodyOutcome(bodyFrames, cancellationRequested);
                    }
                    IReadOnlyDictionary<string, NodeValue>? row = null;
                    if (region.End is not null)
                    {
                        ExecutionFrame end = body[region.End.Id];
                        if (end.Targets.Count == 1)
                        {
                            row = results!.Read(end.Targets[0].Result);
                        }
                        string controlled = "iteration.controlled";
                        bool omitted = control.IsClosed && end.Targets.Count == 1 && (end.Outcome == EOutcome.Succeeded
                            || end.Targets[0].Result.Outcome == EOutcome.NotExecuted && string.Equals(end.Targets[0].Result.ReasonCode, controlled, StringComparison.Ordinal));
                        if (row is null && !omitted && actual == EOutcome.Succeeded)
                        {
                            actual = IterationOutcomes.Combine(actual, EOutcome.Failed);
                        }
                    }
                    ExecutionReport report = new(Guid.NewGuid(), workflow.Definition.Id, workflow.Revision, actual, begin, clock.GetUtcNow(), bodyFrames.Select(Report).ToArray());
                    IterationExecutionResult detail = new(index, ExecutionReportPresentation.Bound(report, 8192));
                    long size = JsonSerializer.SerializeToUtf8Bytes(detail, ContractJson.Options).LongLength;
                    if (row is not null)
                    {
                        size = checked(size + row.Values.Sum(value => value.PayloadSize()));
                    }
                    bool retained = budget.TryRetain(size);
                    using (gate.EnterScope())
                    {
                        foreach (ExecutionFrame frame in bodyFrames)
                        {
                            IterationOutcomes.MergeSummary(summaries, frame, cancellationRequested);
                        }
                        if (retained)
                        {
                            details[index] = detail;
                            if (row is not null)
                            {
                                results!.Store(index, row);
                            }
                        }
                        else
                        {
                            actual = IterationOutcomes.Combine(actual, EOutcome.Failed);
                            problem = "iteration.outputLimit";
                        }
                        outcome = IterationOutcomes.Combine(outcome, actual);
                    }
                }
                finally
                {
                    scopeCompleted?.Invoke(seed[region.Start.Id].SignalId);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(count, concurrency)).Select(_ => WorkerAsync())).ConfigureAwait(false);
        NodeResult? aggregate = null;
        if (region.End is not null && outcome == EOutcome.Succeeded)
        {
            try
            {
                aggregate = results!.Aggregate();
                if (aggregate.Outcome != EOutcome.Succeeded)
                {
                    outcome = aggregate.Outcome;
                    problem = aggregate.ReasonCode;
                }
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                outcome = EOutcome.Failed;
                problem = "iteration.outputDepth";
            }
        }
        NodeResult completion = first.Targets[0].Result;
        if (outcome != EOutcome.Succeeded)
        {
            completion = NodeResult.Failure(problem ?? "iteration.bodyFailed", "The iteration did not complete successfully. Started bodies have been awaited; unstarted elements were not retried.", outcome);
        }
        IterationExecutionSummary summary = new(count, started, details.Where(detail => detail is not null).Select(detail => detail!).ToArray());
        summaries[region.Start.Id] = first with { Outcome = outcome, Targets = [new(first.Targets[0].PeerId, completion)], Iteration = summary };
        if (region.End is not null)
        {
            NodeResult collected = aggregate ?? completion;
            string peer = first.Targets[0].PeerId;
            if (summaries.TryGetValue(region.End.Id, out ExecutionFrame? previous) && previous.Targets.Count != 0)
            {
                peer = previous.Targets[0].PeerId;
            }
            summaries[region.End.Id] = new(region.End.Id, outcome, [new(peer, collected)]);
        }
        foreach (string member in region.Members.Where(member => !summaries.ContainsKey(member)))
        {
            summaries[member] = Skip(member);
        }
        return summaries;
    }

    /// <summary>要素ごとのseedだけにitemを渡します。</summary>
    /// <returns>親の受付先を保持した要素出力です。</returns>
    private static ExecutionFrame Element(ExecutionFrame first, NodeValue value, int index, int count)
    {
        NodeResult result = new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["item"] = value, ["index"] = NodeValue.FromObject(index), ["count"] = NodeValue.FromObject(count), ["start"] = NodeValue.FromObject(true), [IterationNodeTypes.LoopPort] = NodeValue.FromObject(true),
        });
        return first with { Targets = [new(first.Targets[0].PeerId, result)], SignalId = Guid.NewGuid(), ElementStart = true };
    }

    /// <summary>要素の詳細を既存結果の表示・保持境界へ渡します。</summary>
    /// <returns>反復の入れ子も含むノード結果です。</returns>
    private static NodeExecutionReport Report(ExecutionFrame frame) => new(frame.NodeId, frame.Outcome, frame.Targets, frame.FileTransaction, frame.Iteration, frame.PcSelection);

    /// <summary>入口を実行できなかった場合はボディを開始しません。</summary>
    /// <returns>未実行の範囲と入口の実際の問題です。</returns>
    private static Dictionary<string, ExecutionFrame> Unstarted(IterationRegion region, ExecutionFrame first)
    {
        Dictionary<string, ExecutionFrame> result = region.Members.ToDictionary(id => id, Skip, StringComparer.Ordinal);
        result[region.Start.Id] = first;
        if (region.End is not null)
        {
            result[region.End.Id] = first with { NodeId = region.End.Id };
        }
        return result;
    }

    /// <summary>未開始と空配列による省略を副作用の成功としません。</summary>
    /// <returns>実行されなかったボディノードです。</returns>
    private static ExecutionFrame Skip(string id) => new(id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("iteration.notStarted", "This body node was not started for any element.", EOutcome.NotExecuted))]);
}
