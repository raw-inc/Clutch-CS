namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>Core起動時に収集したPTY用Shellの候補です。先頭が未指定時の既定です。</summary>
/// <param name="Name">表示用の実行ファイル名です。</param>
/// <param name="Path">起動時に確認した絶対パスです。</param>
public sealed record TerminalShell(string Name, string Path);
