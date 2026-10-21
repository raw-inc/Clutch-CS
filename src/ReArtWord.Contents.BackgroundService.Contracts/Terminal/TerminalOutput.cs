namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>上限付きのバイト履歴と欠落検出用の位置です。</summary>
/// <param name="Offset">今回の実際の先頭です。</param>
/// <param name="NextOffset">次に読む位置です。</param>
/// <param name="Data">出力のBase64です。</param>
/// <param name="Session">所有権と終了状態です。</param>
public sealed record TerminalOutput(long Offset, long NextOffset, string Data, TerminalView Session);
