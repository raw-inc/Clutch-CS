namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>持続接続の入力または描画完了通知です。</summary>
/// <param name="Type">inputまたはconsumedです。</param>
/// <param name="Request">連番付きの入力です。</param>
/// <param name="Offset">描画が完了した出力位置です。</param>
public sealed record TerminalStreamInput(string Type, TerminalRequest? Request = null, long Offset = 0);
