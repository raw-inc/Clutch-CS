using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>期待版を伴う定義保存要求です。</summary>
/// <param name="OperationId">同一保存操作ID。</param>
/// <param name="Definition">新しい定義。</param>
/// <param name="ExpectedRevision">編集元の版。新規ならnull。</param>
/// <param name="ActivationPolicy">反映方針。</param>
public sealed record WorkflowSaveRequest(Guid OperationId, WorkflowDefinition Definition, string? ExpectedRevision, EActivationPolicy ActivationPolicy = EActivationPolicy.Auto);
