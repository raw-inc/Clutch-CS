namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>制御応答と累計統計。該当しない項目は JSON から省略します。</summary>
internal sealed record PipeEvent(string Type, int? ProtocolVersion = null, AdapterInfo[]? Adapters = null, BackendError[]? Errors = null, bool? Running = null, SourceState[]? Sources = null, long? Received = null, long? Dropped = null, long? Suppressed = null, string? Message = null);
