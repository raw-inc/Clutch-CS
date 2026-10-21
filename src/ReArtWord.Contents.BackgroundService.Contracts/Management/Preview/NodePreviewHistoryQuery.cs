namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>表示を切り替えた一つのノードの短命な履歴だけを照会します。</summary>
public sealed record NodePreviewHistoryQuery(string WorkflowId, string NodeId);
