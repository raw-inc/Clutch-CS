namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>意図された未実行を、終了ログと画面の両方で実行障害から区別します。</summary>
public static class ExecutionResultReasons
{
    /// <summary>必須入力が依存する分岐が、今回の要求では選択されなかった理由です。</summary>
    public static string BranchNotSelected => "input.branchNotSelected";

    private static readonly HashSet<string> expectedSkips = new([
        "selection.configuration", "value.configuration", "input.inactive", "input.eventPending", "iteration.notStarted", "concurrency.notStarted", "iteration.controlled",
        "input.missing", "button.notPressed", "signal.eventBoundary", "condition.notMatched", BranchNotSelected,
        "overlap.running", "overlap.replaced", "overlap.cancelled", "safety.dropped", "safety.replaced", "execution.cancelledBeforeStart", "source.cancelledBeforeExecution"
    ], StringComparer.Ordinal);

    /// <summary>未実行という結果だけでなく、その理由が既知の正常な制御かを確認します。</summary>
    /// <returns>未選択・設定値・待機・指定された間引きなど、障害ではない理由です。</returns>
    public static bool IsExpectedSkip(string? reason) => reason is not null && expectedSkips.Contains(reason);
}
