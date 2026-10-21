using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;

/// <summary>切断中も有効だった編集を共有状態へ統合する要求です。</summary>
/// <param name="OperationId">保存操作。</param>
/// <param name="BaseRevision">編集元の保存版。</param>
/// <param name="Edited">手元の編集結果。</param>
/// <param name="Resolutions">競合項目ごとの人の選択。</param>
/// <param name="ActivationPolicy">今回保存後の反映方針。</param>
public sealed record WorkflowMergeRequest(Guid OperationId, string BaseRevision, WorkflowDefinition Edited, IReadOnlyDictionary<string, EEditResolution> Resolutions, EActivationPolicy ActivationPolicy = EActivationPolicy.Auto);
