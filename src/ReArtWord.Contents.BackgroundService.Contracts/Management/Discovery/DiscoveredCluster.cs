using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

/// <summary>監視処理が参加せずに受信したクラスタの存在情報です。</summary>
/// <param name="ClusterId">発見したクラスタ。</param>
/// <param name="Peers">受信したPeer実体と実状態。</param>
/// <param name="CoordinatorCandidates">各announcementが示した候補。複数時は一致を推定しません。</param>
/// <param name="LastSeen">最後に実受信した時刻。</param>
public sealed record DiscoveredCluster(string ClusterId, IReadOnlyList<PeerInfo> Peers, IReadOnlyList<string> CoordinatorCandidates, DateTimeOffset LastSeen);
