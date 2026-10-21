namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>元の実行と、その実行を追跡するPeer実体を指定した取消要求です。</summary>
/// <param name="OperationId">この取消操作の識別子。</param>
/// <param name="RunId">取り消すワークフロー実行の識別子。</param>
/// <param name="OwnerInstanceId">元の実行を追跡しているプロセス実体。</param>
public sealed record WorkflowCancelRequest(Guid OperationId, Guid RunId, Guid OwnerInstanceId);
