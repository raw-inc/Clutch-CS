namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元中の編集保護です。結果不明の所有者を期限で解除しません。</summary>
public sealed record SnapshotEditLock(Guid OperationId, string WorkflowId, IReadOnlyList<string>? NodeIds, bool IsConfigurationParticipant = false);
