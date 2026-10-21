namespace ReArtWord.Contents.BackgroundService.Contracts.Resources;

/// <summary>ワークフローに依存しないリソース収集と診断履歴の方針です。</summary>
/// <param name="SampleIntervalMilliseconds">共通収集間隔です。</param>
/// <param name="HistoryCapacity">Peerローカル診断履歴の最大件数です。</param>
/// <param name="HistoryRetentionHours">履歴の保持時間です。</param>
/// <param name="NvidiaSmiPath">NVIDIA GPUを取得する場合の明示実行物パスです。</param>
public sealed record ResourceOptions(int SampleIntervalMilliseconds = 3000, int HistoryCapacity = 720, int HistoryRetentionHours = 24, string? NvidiaSmiPath = null);
