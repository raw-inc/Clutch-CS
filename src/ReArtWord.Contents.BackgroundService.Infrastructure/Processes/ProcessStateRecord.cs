using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>再起動で消してはいけないプロセス操作意図と故障履歴です。</summary>
/// <param name="InstanceId">起動回の識別子です。</param>
/// <param name="Identity">最後に確認した実プロセスです。</param>
/// <param name="LaunchPending">起動意図保存後に実起動の確定を保存できていない状態です。</param>
/// <param name="Ready">現起動回に渡された準備状態です。</param>
/// <param name="Healthy">現起動回に渡された利用可能状態です。</param>
/// <param name="RecoverySuppressed">人が設定した復旧抑止です。</param>
/// <param name="PlannedStop">予定停止を要求済みかです。</param>
/// <param name="FailureCount">区切り未確認の故障回数です。</param>
/// <param name="RecoveryAttempts">起動成功を確認するまで保持する復旧試行回数です。</param>
/// <param name="ExitCounted">現起動回の終了を故障回数へ反映したかです。</param>
/// <param name="RecoveryExhausted">有限復旧の上限へ達したかです。</param>
/// <param name="NextRecoveryAt">明示した復旧間隔の次の境界です。</param>
/// <param name="ExitCode">保持した終了コードです。</param>
/// <param name="ExitedAt">今回の終了を確認した時刻です。</param>
/// <param name="RestartRequested">再起動または自動復旧の起動回かです。</param>
/// <param name="ExitUnobserved">監視できない間に終了し、後追い通知・復旧を行わない状態です。</param>
/// <param name="Replacement">自律再起動の確認区間または照合を保留している理由です。</param>
/// <param name="ReplacementProblem">自律再起動の確認区間または照合を保留している理由です。</param>
/// <param name="RecoveryReplacement">終了確定後、復旧を待つ間の後継照合条件です。</param>
/// <param name="RecoveryOrigin">表示用の次回日時を経過時間へ対応付ける起点です。</param>
internal sealed record ProcessStateRecord(Guid InstanceId, ProcessIdentity? Identity = null, bool LaunchPending = false, bool? Ready = null, bool? Healthy = null, bool RecoverySuppressed = false, bool PlannedStop = false, int FailureCount = 0, int RecoveryAttempts = 0, bool ExitCounted = false, bool RecoveryExhausted = false, DateTimeOffset? NextRecoveryAt = null, int? ExitCode = null, DateTimeOffset? ExitedAt = null, bool RestartRequested = false, bool ExitUnobserved = false, ProcessReplacementWindow? Replacement = null, string? ReplacementProblem = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProcessReplacementWindow? RecoveryReplacement = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProcessWaitOrigin? RecoveryOrigin = null)
{
    /// <summary>明示確認された故障区切りを解除し、次の起動前に照合する後継候補の条件を残します。</summary>
    /// <returns>実行回数と不明な待機を解除した状態です。</returns>
    internal ProcessStateRecord ConfirmRecovery(ProcessWaitClock clock, bool running)
        => this with { FailureCount = 0, RecoveryAttempts = 0, RecoveryExhausted = false, NextRecoveryAt = null, RecoveryOrigin = null,
            Replacement = Replacement?.ConfirmUnknownWait(clock), RecoveryReplacement = RecoveryReplacement?.ConfirmUnknownWait(clock), ExitCounted = !running };

    /// <summary>元の復旧待ちが不明なときは、新しい起動を許可しません。</summary>
    /// <returns>待機中・確認不能の理由です。待機がないか満了していればnullです。</returns>
    internal (Contracts.Execution.EOutcome Outcome, string Reason)? WaitProblem(ProcessWaitClock clock)
    {
        TimeSpan? remaining = clock.Remaining(RecoveryOrigin, NextRecoveryAt);
        if (remaining is null)
        {
            return (Contracts.Execution.EOutcome.Unknown, "process.recoveryClockUnconfirmed");
        }
        if (TimeSpan.Zero < remaining)
        {
            return (Contracts.Execution.EOutcome.NotExecuted, "process.recoveryDelay");
        }
        return null;
    }
}
