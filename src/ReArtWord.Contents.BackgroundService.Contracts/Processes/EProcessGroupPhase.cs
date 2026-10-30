namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>アプリケーショングループの操作段階です。</summary>
public enum EProcessGroupPhase
{
    /// <summary>進行中のグループ操作はありません。</summary>
    Idle,

    /// <summary>対象アプリケーションを停止しています。</summary>
    Stopping,

    /// <summary>次の起動時刻を待っています。</summary>
    Waiting,

    /// <summary>設定順にアプリケーションを起動しています。</summary>
    Starting,

    /// <summary>操作を確定できないため、利用者の確認を待っています。</summary>
    Paused,
}
