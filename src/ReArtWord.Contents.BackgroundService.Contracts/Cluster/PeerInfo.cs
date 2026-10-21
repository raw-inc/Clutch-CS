namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster;

/// <summary>発見・同期の結果として確認できたPeerの状態です。</summary>
/// <param name="PeerId">再起動を越えるPeer識別子。</param>
/// <param name="InstanceId">今回起動中の実体。</param>
/// <param name="HostId">Host識別子。</param>
/// <param name="Address">観測した通信先アドレス。</param>
/// <param name="Port">Peer通信ポート。</param>
/// <param name="Status">参加状態。</param>
/// <param name="Priority">Coordinator希望順位。</param>
/// <param name="ReadySince">連続通常参加の開始時刻。</param>
/// <param name="LastSeen">最後に通信を確認した時刻。</param>
/// <param name="LogPort">ログ転送用TCPポート。未起動時はnull。</param>
/// <param name="PersistenceStatus">保存・ローカル変更の件数。未観測の場合はnullです。</param>
/// <param name="BufferedRuntime">実体を固定した照会で確認した対応です。旧版が読む通知本文へ追加しません。</param>
public sealed record PeerInfo(
    string PeerId,
    Guid InstanceId,
    string HostId,
    string Address,
    int Port,
    EPeerStatus Status,
    int Priority,
    DateTimeOffset? ReadySince,
    DateTimeOffset LastSeen,
    int? LogPort = null,
    PeerPersistenceStatus? PersistenceStatus = null,
    [property: System.Text.Json.Serialization.JsonIgnore] bool BufferedRuntime = false);
