namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>削除前保護、共有削除、受付Peerの新規入力停止を区別します。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="WorkflowId">対象です。</param>
/// <param name="Snapshot">削除前スナップショットの結果です。</param>
/// <param name="Deleted">保存定義と有効化希望の共有削除結果です。</param>
/// <param name="Deactivation">受付Peerの新規入力停止結果です。</param>
/// <param name="SnapshotId">復元できる削除前スナップショットです。</param>
public sealed record WorkflowDeleteResult(Guid OperationId, string WorkflowId, ConfigurationPhaseResult Snapshot,
    ConfigurationPhaseResult Deleted, ConfigurationPhaseResult Deactivation, string? SnapshotId = null);
