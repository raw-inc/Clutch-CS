using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>ワークフローの保存版と有効版。</summary>
/// <param name="Saved">保存済みの最新版です。</param>
/// <param name="Effective">書き出し時に有効だった定義。有効な定義がない場合はnull。</param>
public sealed record ArchivedWorkflow(StoredWorkflow Saved, WorkflowDefinition? Effective);
