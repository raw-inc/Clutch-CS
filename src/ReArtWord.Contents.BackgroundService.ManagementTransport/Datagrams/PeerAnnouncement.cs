using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>実参加状態と同期内容の識別情報です。</summary>
/// <param name="Peer">実参加情報。</param>
/// <param name="Digest">実共有内容の検査値。</param>
/// <param name="Coordinator">現在担当。</param>
/// <param name="PreviousCoordinator">分断前担当。</param>
/// <param name="Sequence">同じ送信実体で増加する通知番号。未指定0は受理しません。</param>
public sealed record PeerAnnouncement(PeerInfo Peer, string Digest, string? Coordinator, string? PreviousCoordinator, long Sequence = 0)
{
    /// <summary>時刻を比較せず同じ実体の重複・逆順通知を拒否します。</summary>
    /// <returns>正の新しい通知番号である場合だけtrueです。</returns>
    public bool IsNewerThan(PeerAnnouncement? previous) => 0 < Sequence && (previous is null || Peer.InstanceId != previous.Peer.InstanceId || previous.Sequence < Sequence);
}
