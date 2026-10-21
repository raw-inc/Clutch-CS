using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;

/// <summary>更新対象のサービス寿命から独立して実行する固定クラスタ操作です。</summary>
/// <param name="Request">明示した対象と失敗方針です。</param>
/// <param name="PeerInstances">開始時に固定した各Peerの管理APIです。</param>
/// <param name="Binding">管理通信に使う明示NICとブロードキャスト範囲です。</param>
/// <param name="ClusterId">固定対象クラスタです。</param>
/// <param name="RepositoryPath">installation外の既存永続リポジトリです。</param>
/// <param name="StorageBackend">sqliteまたはjsonです。</param>
/// <param name="CoordinatorPeerId">操作を依頼したPeerです。</param>
public sealed record ClusterUpdateJob(ClusterUpdateRequest Request, IReadOnlyDictionary<string, Guid> PeerInstances, ClientNetworkBinding Binding, string ClusterId, string RepositoryPath, string StorageBackend, string CoordinatorPeerId);
