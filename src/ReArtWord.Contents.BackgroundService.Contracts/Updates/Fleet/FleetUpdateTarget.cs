using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>プレビュー時に固定したクラスタ・host・実体・NICです。</summary>
/// <param name="ClusterId">対象クラスタです。</param>
/// <param name="Peer">対象の発見情報です。</param>
/// <param name="Binding">開始後に差し替えないClientHostのNICです。</param>
public sealed record FleetUpdateTarget(string ClusterId, PeerInfo Peer, ClientNetworkBinding Binding);

