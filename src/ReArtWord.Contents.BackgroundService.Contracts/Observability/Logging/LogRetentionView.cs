using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>共通の保存・有効版と、照会したPeerの実反映を分けた状態です。</summary>
/// <param name="Configuration">共有設定の保存版と有効版。</param>
/// <param name="Local">照会先Peerでの保持方針の反映結果。</param>
public sealed record LogRetentionView(ClusterOperationalState Configuration, LogRetentionStatus? Local);
