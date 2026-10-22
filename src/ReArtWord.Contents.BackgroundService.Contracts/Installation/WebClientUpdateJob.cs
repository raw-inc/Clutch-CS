using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>導入記録と稼働実体を固定した、Webクライアントだけの明示更新です。</summary>
public sealed record WebClientUpdateJob(Guid OperationId, WebClientInstallation Installation, ProcessIdentity PreviousProcess,
    ReleaseManifest Manifest, string PayloadRoot);
