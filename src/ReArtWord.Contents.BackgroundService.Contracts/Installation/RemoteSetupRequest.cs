namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>対象Peer自身の登録済み配置だけに適用する保守要求です。</summary>
public sealed record RemoteSetupRequest(Guid OperationId, ESetupAction Action, Guid InstallationId, bool KeepDatabase = true,
    bool KeepConfiguration = true, bool SurvivorConfirmed = false);
