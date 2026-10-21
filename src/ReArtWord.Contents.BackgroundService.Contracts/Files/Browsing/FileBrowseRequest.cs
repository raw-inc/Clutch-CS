namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;

/// <summary>選択先ピアの許可範囲を読み取り専用で閲覧します。</summary>
/// <param name="Path">未指定時は許可ルート一覧です。</param>
/// <param name="Offset">同じディレクトリの次ページ位置です。</param>
/// <param name="FromDrives">ドライブからOSの読取り権限で参照するかです。転送の許可範囲は変更しません。</param>
public sealed record FileBrowseRequest(string? Path = null, int Offset = 0, bool FromDrives = false);
