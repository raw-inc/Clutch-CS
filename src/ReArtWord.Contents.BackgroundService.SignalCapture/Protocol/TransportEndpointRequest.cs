namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>TCP / UDP を区別する明示 IP とポートの除外指定。</summary>
internal sealed record TransportEndpointRequest(string Transport, string Address, int Port);
