namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>長さ検証済みの制御フレーム。</summary>
internal sealed record PipeFrame(byte Kind, byte[] Payload);
