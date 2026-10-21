namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>ファイル転送の列挙継続位置です。</summary>
/// <param name="Path">列挙対象の絶対パスです。</param>
/// <param name="Continuation">取得済みの最後のパスです。</param>
public sealed record FileTransferListRequest(string Path, string? Continuation = null);
