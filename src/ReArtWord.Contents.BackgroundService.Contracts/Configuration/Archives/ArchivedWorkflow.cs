using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>未反映の保存版を有効版に置き換えず保持します。</summary>
/// <param name="Saved">保存済みの最新版です。</param>
/// <param name="Effective">実際に有効だった定義です。nullは未有効化を表します。</param>
public sealed record ArchivedWorkflow(StoredWorkflow Saved, WorkflowDefinition? Effective);
