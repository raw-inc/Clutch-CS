namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>Coreの導入権限と分けて実行するWebクライアントの導入要求です。</summary>
public sealed record WebClientSetupJob(Guid OperationId, InstallationSettings Settings);
