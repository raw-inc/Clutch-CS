namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>旧版復帰を確認できないPeerがある場合の明示方針です。</summary>
public enum EUpdateRollbackFailurePolicy
{
    /// <summary>失敗Peerを通常参加から外し残存Peerを維持します。</summary>
    ExcludeFailed,

    /// <summary>対象クラスタの通常処理を停止します。</summary>
    StopCluster,
}
