namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>一つの登録アプリケーションの観測・復旧状態です。</summary>
/// <param name="Id">アプリケーション識別子です。</param>
/// <param name="Mode">明示管理境界です。</param>
/// <param name="InstanceId">一回の起動を区別する識別子です。</param>
/// <param name="Identity">OS上の実プロセス識別子です。</param>
/// <param name="Running">確認できた生存状態。nullは判定不能です。</param>
/// <param name="Ready">現起動回に渡された準備状態です。</param>
/// <param name="Healthy">現起動回に渡された利用可能状態です。</param>
/// <param name="RecoverySuppressed">人が明示した復旧抑止です。</param>
/// <param name="PlannedStop">予定停止として停止を要求した状態です。</param>
/// <param name="FailureCount">同じ故障で観測した異常回数です。</param>
/// <param name="RecoveryAttempts">起動成功をまだ確認できていない復旧試行数です。起動成功時に0へ戻します。</param>
/// <param name="RecoveryExhausted">復旧回数の上限へ達した状態です。</param>
/// <param name="Reason">判定不能や停止理由の安定した種類です。</param>
/// <param name="ExitCode">確認した終了コードです。未取得を0に置換しません。</param>
/// <param name="ExitedAt">この起動回の終了を確認した時刻です。</param>
/// <param name="NextRecoveryAt">終了後の再起動待ち時刻です。</param>
/// <param name="ExitUnobserved">監視停止中の終了など、終了事象を観測できなかった状態です。</param>
/// <param name="ReplacementDeadline">自律再起動の確認区間または照合を保留している理由です。</param>
/// <param name="ReplacementProblem">終了確認または復旧待機で、後継の照合を保留している理由です。</param>
public sealed record ProcessStatus(string Id, EProcessMode Mode, Guid InstanceId, ProcessIdentity? Identity, bool? Running, bool? Ready, bool? Healthy,
    bool RecoverySuppressed, bool PlannedStop, int FailureCount, int RecoveryAttempts, bool RecoveryExhausted, string? Reason = null,
    int? ExitCode = null, DateTimeOffset? ExitedAt = null, DateTimeOffset? NextRecoveryAt = null, bool ExitUnobserved = false, DateTimeOffset? ReplacementDeadline = null, string? ReplacementProblem = null);
