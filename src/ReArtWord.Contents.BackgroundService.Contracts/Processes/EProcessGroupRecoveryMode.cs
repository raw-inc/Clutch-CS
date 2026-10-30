namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>グループ内のアプリケーションが終了した場合の復旧範囲です。</summary>
public enum EProcessGroupRecoveryMode
{
    /// <summary>終了したアプリケーションだけを復旧します。</summary>
    FailedApplications,

    /// <summary>全員の停止を確認してから、設定順に起動します。</summary>
    EntireGroup,
}
