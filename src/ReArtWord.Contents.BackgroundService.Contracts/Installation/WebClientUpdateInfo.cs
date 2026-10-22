using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>更新対象の導入記録と、照合済みの稼働実体です。</summary>
public sealed record WebClientUpdateInfo(WebClientInstallation Installation, ClientUpdateHealth Health);
