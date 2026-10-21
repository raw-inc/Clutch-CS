namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>再取得を抑制する TCP endpoint。</summary>
internal sealed record EndpointRequest(string Address, int Port);
