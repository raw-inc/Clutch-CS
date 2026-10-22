using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>接続先が所有するWebクライアントに対する、転送前の固定要求です。</summary>
public sealed record WebClientUpdateRequest(Guid OperationId, Guid InstallationId, ReleaseManifest Manifest);
