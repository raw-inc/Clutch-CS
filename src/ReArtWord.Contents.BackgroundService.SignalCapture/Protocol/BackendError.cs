namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>バックエンド単位の利用可否。</summary>
internal sealed record BackendError(string Backend, string Message);
