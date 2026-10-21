using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;

/// <summary>区間を一度実行し、正常・異常・取消にかかわらず開始済みの子と枠を回収します。</summary>
internal sealed class ConcurrencyRegionRunner(WorkflowConcurrencyExecution concurrency, IterationScopeRunner scopes,
    Func<Guid, Task<EOutcome>> completeScope, Action<Guid, Guid> inheritProgress)
{
    /// <summary>反復と同じ区間所有を使い、終了境界より先の処理には枠を持ち越しません。</summary>
    /// <returns>一回だけ実行したボディと境界の結果です。</returns>
    internal async Task<Dictionary<string, ExecutionFrame>> RunAsync(IterationRegion region, ExecutionFrame first,
        Dictionary<string, ExecutionFrame> available, Func<string, Task<ExecutionFrame>?> read, IterationControl? parentControl, Guid parentScope,
        CancellationToken cancellationToken = default)
    {
        if (first.Outcome != EOutcome.Succeeded || first.Targets.Count != 1 || first.ConfigurationOnly && !first.RetainedValue)
        {
            return Unstarted(region, first);
        }
        WorkflowConcurrencyAdmission admission;
        try
        {
            admission = await concurrency.AcquireAsync(region.Start, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            admission = new(null, NodeResult.Failure("concurrency.cancelled", "The waiting execution was cancelled.", EOutcome.Cancelled));
        }
        if (admission.Lease is null)
        {
            NodeResult rejected = admission.Rejection ?? NodeResult.Failure("concurrency.unconfirmed", "The concurrency permit could not be confirmed.", EOutcome.Unknown);
            concurrency.Abort();
            return Unstarted(region, first with { Outcome = rejected.Outcome, Targets = [new(first.Targets[0].PeerId, rejected)] });
        }
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lease = admission.Lease.ConfigureAwait(false);
        Guid scope = Guid.NewGuid();
        inheritProgress(scope, parentScope);
        Dictionary<string, NodeValue> outputs = first.Targets[0].Result.Outputs.Where(pair => !CompletionPorts.IsPort(pair.Key)).ToDictionary(StringComparer.Ordinal);
        NodeResult entered = first.Targets[0].Result with { Outputs = outputs };
        ExecutionFrame seed = first with { Targets = [new(first.Targets[0].PeerId, entered)], SignalId = scope, ElementStart = true };
        Dictionary<string, ExecutionFrame> inherited = new(available, StringComparer.Ordinal) { [region.Start.Id] = seed };
        Dictionary<string, ExecutionFrame> finished;
        EOutcome children = EOutcome.Succeeded;
        try
        {
            finished = await scopes.RunAsync(region.Members, region.Start.Id, inherited, [], null, parentControl, read, seed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            children = await completeScope(scope).ConfigureAwait(false);
        }
        ExecutionFrame[] body = finished.Values.Where(frame => !string.Equals(frame.NodeId, region.Start.Id, StringComparison.Ordinal)).ToArray();
        EOutcome outcome = ExecutionOutcomes.Aggregate(body.Select(frame => frame.Outcome));
        if (outcome != EOutcome.Cancelled)
        {
            outcome = IterationOutcomes.BodyOutcome(body);
        }
        outcome = ExecutionOutcomes.Aggregate([outcome, children]);
        if (outcome == EOutcome.Succeeded && body.Any(frame => frame.AwaitingEvent))
        {
            // 起動待ちを完了通知にしません。後着事象はこの値を借り、新たな枠内で必要な後続だけを実行します。
            finished[region.Start.Id] = seed with { AwaitingEvent = true };
            return finished;
        }
        finished[region.Start.Id] = Complete(first, outcome);
        if (region.End is not null)
        {
            finished[region.End.Id] = Complete(finished[region.End.Id], outcome);
        }
        return finished;
    }

    /// <summary>区間の実成否を共通完了出力へ反映し、入力値は保持します。</summary>
    /// <returns>区間全体の結果として公開する境界です。</returns>
    private static ExecutionFrame Complete(ExecutionFrame frame, EOutcome outcome)
    {
        TargetNodeResult[] targets = frame.Targets.Select(target =>
        {
            Dictionary<string, NodeValue> outputs = new(target.Result.Outputs, StringComparer.Ordinal)
            {
                [CompletionPorts.Signal] = NodeValue.FromObject(outcome is EOutcome.Succeeded or EOutcome.Failed),
                [CompletionPorts.Success] = NodeValue.FromObject(CompletionPorts.Status(outcome)),
            };
            string route = "success";
            if (outcome != EOutcome.Succeeded)
            {
                route = NodeResult.Failure("concurrency.body", "The concurrency region did not succeed.", outcome).Route;
            }
            return target with { Result = target.Result with
            {
                Outcome = outcome, Route = route, Outputs = outputs,
                ReasonCode = outcome == EOutcome.Succeeded ? target.Result.ReasonCode
                                                           : target.Result.ReasonCode ?? "concurrency.body",
                Message = outcome == EOutcome.Succeeded ? target.Result.Message
                                                        : target.Result.Message ?? "The concurrency region did not succeed. Inspect the downstream node results.",
            } };
        }).ToArray();
        return frame with { Outcome = outcome, Targets = targets, ElementStart = false, ConfigurationOnly = false, RetainedValue = false, AwaitingEvent = false };
    }

    /// <summary>入口が通過できない場合、ボディを成功扱いせず未開始として残します。</summary>
    /// <returns>開始境界の理由を保持する区間全体の結果です。</returns>
    private static Dictionary<string, ExecutionFrame> Unstarted(IterationRegion region, ExecutionFrame first)
    {
        Dictionary<string, ExecutionFrame> frames = region.Members.ToDictionary(id => id,
            id => new ExecutionFrame(id, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("concurrency.notStarted", "The concurrency region was not started.", EOutcome.NotExecuted))]), StringComparer.Ordinal);
        frames[region.Start.Id] = first;
        if (region.End is not null)
        {
            frames[region.End.Id] = first with { NodeId = region.End.Id };
        }
        return frames;
    }
}
