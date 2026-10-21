using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>要素内の未実行と実際の失敗を区別して反復の結果を集約します。</summary>
internal static class IterationOutcomes
{
    /// <summary>非選択枝の未実行と、開始した反復全体の未完了を区別します。</summary>
    /// <returns>より重い実際の成否です。</returns>
    internal static EOutcome Combine(EOutcome current, EOutcome next)
    {
        if (next == EOutcome.NotExecuted)
        {
            next = EOutcome.Failed;
        }
        return ExecutionOutcomes.Aggregate([current, next]);
    }

    /// <summary>非選択経路以外の未実行を、別枝の成功で覆いません。</summary>
    /// <returns>実施できなかった処理も含む要素全体の結果です。</returns>
    internal static EOutcome BodyOutcome(ExecutionFrame[] frames, bool cancellationRequested = false)
    {
        EOutcome outcome = ExecutionOutcomes.Aggregate(frames.Select(frame => CancelledOutcome(frame, cancellationRequested)));
        if (HasBlockedBody(frames, cancellationRequested))
        {
            return Combine(outcome, EOutcome.Failed);
        }
        if (outcome == EOutcome.NotExecuted)
        {
            return EOutcome.Succeeded;
        }
        return outcome;
    }

    /// <summary>意図された非選択以外に、開始できなかった処理があるか確認します。</summary>
    /// <returns>反復の未完了として扱う未実行がある場合はtrueです。</returns>
    private static bool HasBlockedBody(IEnumerable<ExecutionFrame> frames, bool cancellationRequested)
    {
        string[] inactive = ["input.missing", "input.inactive", "input.eventPending", "signal.eventBoundary", "selection.configuration", "value.configuration", "iteration.notStarted", "concurrency.notStarted", "iteration.controlled", "condition.notMatched", ExecutionResultReasons.BranchNotSelected];
        return frames.SelectMany(frame => frame.Targets).Any(target => target.Result.Outcome == EOutcome.NotExecuted
            && !inactive.Contains(target.Result.ReasonCode, StringComparer.Ordinal) && !(cancellationRequested && ExecutionOutcomes.CancelledBeforeStart(target.Result)));
    }

    /// <summary>外側へはボディの値を漏らさず、対象別の実際の成否だけ集約します。</summary>
    internal static void MergeSummary(Dictionary<string, ExecutionFrame> summaries, ExecutionFrame frame, bool cancellationRequested)
    {
        List<TargetNodeResult> targets = [];
        ExecutionFrame? earlier = summaries.GetValueOrDefault(frame.NodeId);
        IEnumerable<TargetNodeResult> previous = earlier?.Targets ?? [];
        foreach (IGrouping<string, TargetNodeResult> group in previous.Concat(frame.Targets).GroupBy(target => target.PeerId, StringComparer.Ordinal))
        {
            EOutcome outcome = ExecutionOutcomes.Aggregate(group.Select(target => target.Result.Outcome));
            NodeResult representative = group.First(target => target.Result.Outcome == outcome).Result;
            targets.Add(new(group.Key, representative with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) }));
        }
        EOutcome current = CancelledOutcome(frame, cancellationRequested);
        if (HasBlockedBody([frame], cancellationRequested))
        {
            current = Combine(current, EOutcome.Failed);
        }
        EOutcome combined = ExecutionOutcomes.Aggregate([earlier?.Outcome ?? EOutcome.NotExecuted, current]);
        summaries[frame.NodeId] = frame with { Outcome = combined, Targets = targets, FileTransaction = null, Iteration = null };
    }

    /// <summary>開始前の取消を集約へ反映し、実失敗や結果不明は優先します。</summary>
    /// <returns>対象別の未実行記録を保ったまま集約する成否です。</returns>
    private static EOutcome CancelledOutcome(ExecutionFrame frame, bool cancellationRequested)
    {
        if (cancellationRequested && frame.Targets.Any(target => ExecutionOutcomes.CancelledBeforeStart(target.Result)))
        {
            return ExecutionOutcomes.Aggregate([frame.Outcome, EOutcome.Cancelled]);
        }
        return frame.Outcome;
    }
}
