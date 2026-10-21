namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>画面や接続選択から独立した共有進捗です。</summary>
/// <param name="Request">固定した要求です。</param>
/// <param name="State">Queued、Copying、Succeeded、Failed、Unknownです。</param>
/// <param name="BytesCopied">書込みを確認したバイト数です。</param>
/// <param name="TotalBytes">列挙済みの総バイト数です。</param>
/// <param name="UpdatedAt">進捗時刻です。</param>
/// <param name="Problem">失敗または確認不能の説明です。</param>
public sealed record FileTransferJob(FileTransferRequest Request, string State, long BytesCopied, long TotalBytes, DateTimeOffset UpdatedAt, string? Problem = null);
