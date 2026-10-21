namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>内容を転送せずに取得するファイルまたはディレクトリの同一性です。</summary>
/// <param name="Path">Pathの指定値。</param>
/// <param name="IsDirectory">IsDirectoryの指定値。</param>
/// <param name="Length">Lengthの指定値。</param>
/// <param name="Sha256">Sha256の指定値。</param>
public sealed record FileEntryInfo(string Path, bool IsDirectory, long Length, string Sha256);
