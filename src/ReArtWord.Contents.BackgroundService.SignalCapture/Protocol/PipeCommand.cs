namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>利用側からの制御要求。</summary>
internal sealed record PipeCommand(string Type, SourceRequest[]? Sources = null, EndpointRequest[]? ExcludedTcpEndpoints = null, TransportEndpointRequest[]? ExcludedEndpoints = null);
