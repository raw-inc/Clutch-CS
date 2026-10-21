namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>実際に参照した接続と、その入力事象を識別します。</summary>
public sealed record WorkflowDebugConnection(string FromNode, string FromPort, string ToPort, string Route, bool Available, bool Inherited, Guid SignalId, string? Path = null);
