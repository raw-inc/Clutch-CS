using ReArtWord.Contents.BackgroundService.Contracts.Files;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>メディア処理の実行物と許可ファイル範囲を固定します。</summary>
/// <param name="FfmpegPath">明示したffmpeg実行ファイルの絶対パスです。</param>
/// <param name="FfprobePath">明示したffprobe実行ファイルの絶対パスです。</param>
/// <param name="Files">入力と出力に共通のファイル許可方針です。</param>
public sealed record MediaOptions(string FfmpegPath, string FfprobePath, FileSystemOptions Files);
