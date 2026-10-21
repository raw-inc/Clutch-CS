namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>単独実行更新で確認済みの進行段階です。</summary>
public enum EUpdatePhase
{
    /// <summary>副作用前のペイロードを分割転送しています。</summary>
    Transferring,

    /// <summary>ペイロードと設定の準備を確認しました。</summary>
    Prepared,

    /// <summary>副作用開始意図を保存しました。</summary>
    Stopping,

    /// <summary>旧プロセスの終了を確認しました。</summary>
    Stopped,

    /// <summary>旧リリースの退避を確認しました。</summary>
    BackedUp,

    /// <summary>検証済みリリースへ切り替えました。</summary>
    Installed,

    /// <summary>起動要求を発行しました。</summary>
    Starting,

    /// <summary>確認済み旧リリースへ戻す意図を保存しました。</summary>
    Restoring,

    /// <summary>新実体のreadyとリリースを確認しました。</summary>
    Completed,

    /// <summary>明示復元先のreadyを確認しました。</summary>
    RolledBack,

    /// <summary>既知の失敗を確認しました。</summary>
    Failed,

    /// <summary>途中停止または副作用の結果を確認できません。</summary>
    Interrupted,
}
