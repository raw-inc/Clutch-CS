namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>元WFと配置ノードを指定する読み取り要求です。</summary>
public sealed record WorkflowRecipePreviewQuery(string WorkflowId, string NodeId, string? KnownHash = null);
