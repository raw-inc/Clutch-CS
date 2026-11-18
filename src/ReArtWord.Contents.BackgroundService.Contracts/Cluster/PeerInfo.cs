using ReArtWord.Contents.BackgroundService.Contracts.Observability;

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
    [property: System.Text.Json.Serialization.JsonIgnore] bool BufferedRuntime = false)
{
    /// <summary>この観測元が通信を受理してからの経過時間です。保存やPeer間通知へは含めません。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ObservationAge? ObservationAge
    {
        get; init;
    }

    /// <summary>観測元の経過時間を優先し、経過時間を含まない旧形式だけは同じ観測元の日時で比較します。</summary>
    /// <returns>最後の受信からの時間です。旧形式の未来日時は負の値を保持します。</returns>
    public TimeSpan AgeAt(DateTimeOffset observerNow) => ObservationAge?.Elapsed ?? observerNow - LastSeen;
}
