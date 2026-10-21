namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>予定停止の通常終了が失敗した後の明示方針です。</summary>
public enum EProcessStopPolicy
{
    /// <summary>動作を残して人の対応を待ちます。</summary>
    WaitForManual,

    /// <summary>明示されたプロセスの強制終了を要求して実終了を確認します。</summary>
    Force,
}
