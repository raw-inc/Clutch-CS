namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

/// <summary>リリース内の一つの実ファイルの検証情報です。</summary>
/// <param name="Path">スラッシュ区切りの相対パスです。</param>
/// <param name="Size">正確なバイト数です。</param>
/// <param name="Sha256">小文字または大文字のSHA256 hexです。</param>
/// <param name="Executable">Unixで実行権限を付与するファイルです。</param>
public sealed record ReleaseFile(string Path, long Size, string Sha256, bool Executable = false);
