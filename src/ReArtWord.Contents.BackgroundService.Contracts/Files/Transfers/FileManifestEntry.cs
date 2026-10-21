namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>転送treeの相対パスと確認する内容です。</summary>
/// <param name="RelativePath">転送rootからの相対パス。root自体は空文字。</param>
/// <param name="IsDirectory">ディレクトリか。</param>
/// <param name="Length">ファイルsize。</param>
/// <param name="Sha256">内容digest。</param>
public sealed record FileManifestEntry(string RelativePath, bool IsDirectory, long Length, string Sha256);
