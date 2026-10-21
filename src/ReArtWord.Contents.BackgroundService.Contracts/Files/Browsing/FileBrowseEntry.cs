namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;

/// <summary>内容を読み取らず返す選択候補です。</summary>
/// <param name="Name">表示用のファイル名。</param>
/// <param name="Path">そのピア上の絶対パス。</param>
/// <param name="IsDirectory">開いて閲覧できるディレクトリか。</param>
public sealed record FileBrowseEntry(string Name, string Path, bool IsDirectory);
