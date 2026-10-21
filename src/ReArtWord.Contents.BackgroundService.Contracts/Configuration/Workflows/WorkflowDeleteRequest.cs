namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>確認した保存版だけを削除する要求です。</summary>
/// <param name="OperationId">再送でも変えない操作IDです。</param>
/// <param name="WorkflowId">削除対象です。</param>
/// <param name="ExpectedRevision">利用者が確認した保存版です。</param>
public sealed record WorkflowDeleteRequest(Guid OperationId, string WorkflowId, string ExpectedRevision);
