namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>実行地点で検証するファイルの読書き範囲です。</summary>
public sealed record FilePathAccess(string PeerId, string Path, bool Write, bool DirectoryEntriesOnly = false);
