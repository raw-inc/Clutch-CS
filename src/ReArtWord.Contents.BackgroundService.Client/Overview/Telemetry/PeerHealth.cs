using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>同じ実体の稼働確認と、通常参加とは独立した背景履歴の進捗を読み取ります。</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
internal sealed record PeerHealth(string ClusterId, string PeerId, Guid InstanceId, EPeerStatus Status, string? BuildVersion,
    HistorySynchronizationStatus? HistorySynchronization = null);
