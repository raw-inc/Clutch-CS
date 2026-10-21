namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>各取得元の開始・停止結果。</summary>
internal sealed record SourceState(string Backend, string AdapterId, bool Running, string Error);
