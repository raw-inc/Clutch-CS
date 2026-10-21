namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>表示中のワークフロー内の必要なノードだけを照会します。</summary>
public sealed record NodePreviewQuery(string WorkflowId, IReadOnlyList<string> NodeIds);
