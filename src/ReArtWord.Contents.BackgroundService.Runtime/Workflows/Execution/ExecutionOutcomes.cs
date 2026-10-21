using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>実対象とグラフ全体で同じ結果の優先順を使います。</summary>
internal static class ExecutionOutcomes
{
    /// <summary>開始された経路の必須入力不足を、入口ボタンだけの成功で隠しません。</summary>
    /// <returns>各処理の未実行を維持し、要求したワークフロー全体の未達を失敗として示します。</returns>
    internal static EOutcome AggregateWorkflow(IReadOnlyList<ExecutionFrame> frames)
    {
        EOutcome outcome = Aggregate(frames.Select(static frame => frame.Outcome));
        string unavailable = "input.unavailable";
        if (outcome is EOutcome.Succeeded or EOutcome.NotExecuted
            && frames.Any(frame => frame.Targets.Any(target => string.Equals(target.Result.ReasonCode, unavailable, StringComparison.Ordinal))))
        {
            return EOutcome.Failed;
        }
        return outcome;
    }

    /// <summary>取消により副作用を開始していないと確認できる結果だけを判定します。</summary>
    /// <returns>開始後の結果不明や、取消以外の未実行ではfalseです。</returns>
    internal static bool CancelledBeforeStart(NodeResult result)
    {
        string beforeStart = "execution.cancelledBeforeStart", unconfirmed = "execution.cancelUnconfirmed",
            overlap = "overlap.cancelled", source = "source.cancelledBeforeExecution";
        return result.Outcome == EOutcome.NotExecuted && (string.Equals(result.ReasonCode, beforeStart, StringComparison.Ordinal)
            || string.Equals(result.ReasonCode, unconfirmed, StringComparison.Ordinal) || string.Equals(result.ReasonCode, overlap, StringComparison.Ordinal)
            || string.Equals(result.ReasonCode, source, StringComparison.Ordinal));
    }

    /// <summary>対象別結果から一回全体の結果を決めます。</summary>
    /// <returns>不明、失敗、取消、成功、未実行の順に判定した結果です。</returns>
    internal static EOutcome Aggregate(IEnumerable<EOutcome> values)
    {
        EOutcome[] outcomes = values.ToArray();
        if (outcomes.Contains(EOutcome.Unknown))
        {
            return EOutcome.Unknown;
        }
        if (outcomes.Contains(EOutcome.Failed))
        {
            return EOutcome.Failed;
        }
        if (outcomes.Contains(EOutcome.Cancelled))
        {
            return EOutcome.Cancelled;
        }
        if (outcomes.Contains(EOutcome.Succeeded))
        {
            return EOutcome.Succeeded;
        }
        return EOutcome.NotExecuted;
    }
}
