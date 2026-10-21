using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>明示削除までPC healthに保持する、最後の実測と所属情報です。</summary>
public sealed record DashboardHostPeer(string ClusterId, PeerInfo Peer, ResourceSample? Sample = null, bool Online = false);
