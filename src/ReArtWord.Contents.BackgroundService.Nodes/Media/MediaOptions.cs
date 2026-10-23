using ReArtWord.Contents.BackgroundService.Contracts.Files;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>メディア処理の実行物と許可ファイル範囲を固定します。</summary>
/// <param name="FfmpegPath">明示したffmpeg実行ファイルの絶対パスです。</param>
/// <param name="FfprobePath">明示したffprobe実行ファイルの絶対パスです。</param>
/// <param name="Files">入力と出力に共通のファイル許可方針です。</param>
public sealed record MediaOptions(string FfmpegPath, string FfprobePath, FileSystemOptions Files)
{
    /// <summary>明示した場合だけ、処理段階・例外・外部コマンドの診断を通知します。標準エラーは入力内容を含む場合があります。</summary>
    public Action<string>? DiagnosticOutput
    {
        get; init;
    }
}
