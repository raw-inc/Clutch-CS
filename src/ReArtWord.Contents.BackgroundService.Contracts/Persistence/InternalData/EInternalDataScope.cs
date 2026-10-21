namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>内部状態を共有ワークフローまたは指定Peerへ限定します。</summary>
public enum EInternalDataScope
{
    /// <summary>ワークフロー共有状態です。</summary>
    Shared,

    /// <summary>選択したPeerのローカル状態です。</summary>
    Peer,

    /// <summary>クラスタ内の全ワークフローから参照する永続状態です。</summary>
    Cluster,
}
