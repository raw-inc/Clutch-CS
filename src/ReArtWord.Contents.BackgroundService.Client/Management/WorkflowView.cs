using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>保存版と現在の有効版を別々に保持します。</summary>
internal sealed record WorkflowView(StoredWorkflow? Saved, WorkflowDefinition? Effective, string? EffectiveRevision, WorkflowListSummary? Summary = null, string? DetailReason = null)
{
    /// <summary>省略された本文を空の編集定義として扱いません。</summary>
    /// <returns>完全に取得できた保存版です。</returns>
    internal StoredWorkflow RequireSaved() => Saved ?? throw UiError.InvalidOperation("The full saved workflow is unavailable. Editing and saving will not start. {0}", DetailReason);
}
