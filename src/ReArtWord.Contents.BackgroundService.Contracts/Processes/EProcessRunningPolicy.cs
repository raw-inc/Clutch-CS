namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>予定時刻に対象が起動済みだった場合の動作です。</summary>
public enum EProcessRunningPolicy
{
    /// <summary>既存プロセスを維持し、この予定を見送ります。</summary>
    Skip,

    /// <summary>既存プロセスの終了を確認してから一回起動します。</summary>
    Restart,
}
