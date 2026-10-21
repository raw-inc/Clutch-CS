using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>独立helperへ渡す固定操作です。既存配置は識別子を一致させます。</summary>
public sealed record SetupJob(Guid OperationId, ESetupAction Action, InstallationSettings Settings, Guid? InstallationId = null,
    bool KeepDatabase = true, bool KeepConfiguration = true, ProcessIdentity? PreviousProcess = null,
    bool ServeResult = false, bool SurvivorConfirmed = false);
