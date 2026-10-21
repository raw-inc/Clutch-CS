namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>Peer内の取得番号と値の概要です。画像・音声・動画の本文は保持しません。</summary>
public sealed record NodePreviewHistoryEntry(long Number, NodePreviewSnapshot Snapshot, string? Text, bool TextTruncated = false);
