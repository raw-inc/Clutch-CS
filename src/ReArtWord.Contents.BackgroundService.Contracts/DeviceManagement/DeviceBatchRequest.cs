namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>画面から開始する一括変更の固定計画です。</summary>
/// <param name="OperationId">一括操作の識別子。</param>
/// <param name="Changes">適用する順序と対象。</param>
/// <param name="AcknowledgeUnconfirmed">以前の不明結果を確認し、新しい変更を明示的に開始するか。</param>
public sealed record DeviceBatchRequest(Guid OperationId, IReadOnlyList<DevicePlannedChange> Changes, bool AcknowledgeUnconfirmed = false);
