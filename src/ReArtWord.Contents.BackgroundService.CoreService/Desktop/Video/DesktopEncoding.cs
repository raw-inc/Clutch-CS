using ReArtWord.Contents.BackgroundService.Contracts.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;

/// <summary>対象画面で実際に初期化できた取得方式と映像エンコーダです。</summary>
/// <param name="Name">FFmpeg encoder名。</param>
/// <param name="Duplication">変更通知付きDXGI取得を使うか。</param>
/// <param name="Codec">使用する映像コーデック。</param>
internal sealed record DesktopEncoding(string Name, bool Duplication, EDesktopVideoCodec Codec = EDesktopVideoCodec.H264);
