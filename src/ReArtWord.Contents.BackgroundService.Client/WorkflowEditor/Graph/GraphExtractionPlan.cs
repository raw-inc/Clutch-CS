using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>元文書が変わっていないことを確認してから適用する変換結果です。</summary>
internal sealed record GraphExtractionPlan(WorkflowDefinition Original, WorkflowDefinition Parent, WorkflowDefinition Recipe, string InstanceId);
