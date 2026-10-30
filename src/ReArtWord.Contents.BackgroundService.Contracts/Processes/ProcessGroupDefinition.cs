namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>同じPC内の登録アプリケーションをまとめて監視・操作する設定です。</summary>
/// <param name="Id">表示名を変更しても維持するグループIDです。</param>
/// <param name="Name">画面とログに表示する名前です。</param>
/// <param name="Members">起動順に並べた登録アプリケーションです。</param>
/// <param name="AutomaticMonitoring">グループとして自動復旧するかです。</param>
/// <param name="RecoveryMode">終了したアプリケーションだけ、または全員を復旧するかです。</param>
/// <param name="RecoveryDelayMilliseconds">全体の自動復旧で、障害確認から再起動まで待つ時間です。</param>
/// <param name="StopTimeoutMilliseconds">一つのアプリケーションの通常終了を待つ上限です。</param>
/// <param name="StopPolicy">通常終了しなかった場合の処理です。</param>
public sealed record ProcessGroupDefinition(string Id, string Name, IReadOnlyList<ProcessGroupMember> Members,
    bool AutomaticMonitoring = true, EProcessGroupRecoveryMode RecoveryMode = EProcessGroupRecoveryMode.FailedApplications,
    int RecoveryDelayMilliseconds = 1000, int StopTimeoutMilliseconds = 30000, EProcessStopPolicy StopPolicy = EProcessStopPolicy.Force);
