namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>通常の新規実行を保留している明示操作です。</summary>
/// <param name="OperationId">解除できる操作の識別子。</param>
/// <param name="Reason">保留する用途と理由。</param>
/// <param name="EnteredAt">保留を保存した時刻。</param>
public sealed record MaintenanceState(Guid OperationId, string Reason, DateTimeOffset EnteredAt);
