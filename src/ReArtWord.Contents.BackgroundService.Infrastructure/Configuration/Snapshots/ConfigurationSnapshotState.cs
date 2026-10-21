using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>補償時に保存版と実有効版を別々に戻します。</summary>
/// <param name="Saved">開始前の保存版。</param>
/// <param name="Effective">開始前に実際に有効だった定義。</param>
internal sealed record ConfigurationSnapshotState(StoredWorkflow Saved, WorkflowDefinition? Effective);
