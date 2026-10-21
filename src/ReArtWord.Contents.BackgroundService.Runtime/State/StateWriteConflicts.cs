using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.State;

namespace ReArtWord.Contents.BackgroundService.Runtime.State;

/// <summary>保存結果不明と、未適用が確定した版競合を区別します。</summary>
public static class StateWriteConflicts
{
    /// <summary>返却された現在版を使って比較更新をやり直せる結果だけを選びます。</summary>
    /// <returns>副作用の再実行ではなく、未適用の状態更新を再計算できるかです。</returns>
    public static bool IsConflict(StateWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !result.Applied && (result.Outcome == EOutcome.Succeeded
            || result.Outcome == EOutcome.Failed && result.Reason is "Persistence.VersionConflict" or "State.VersionConflict");
    }
}
