namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>一時デバッグ記録を有限件数ずつ取得する要求です。</summary>
public sealed record WorkflowDebugQuery(Guid Id, int Offset = 0, int Limit = 20);
