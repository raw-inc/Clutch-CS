namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>バックエンドと取得対象の組合せ。</summary>
internal sealed record SourceRequest(string Backend, string AdapterId);
