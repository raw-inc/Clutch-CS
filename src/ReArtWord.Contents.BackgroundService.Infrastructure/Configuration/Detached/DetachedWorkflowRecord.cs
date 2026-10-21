using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Detached;

/// <summary>ローカル版の元共有状態と、一度固定した再統合要求を保持します。</summary>
internal sealed record DetachedWorkflowRecord(StoredWorkflow? Base, StoredWorkflow Local, bool Pending = true, WorkflowSaveRequest? Integration = null);
