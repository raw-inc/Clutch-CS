namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>プロセス管理の共通既定を保持します。</summary>
/// <param name="DefaultRecoveryAttempts">同じ故障に対する復旧試行上限。nullは明示した無制限です。</param>
/// <param name="DefaultReadyTimeoutMilliseconds">準備待ちの共通上限。nullは明示した無期限です。</param>
/// <param name="RecoveryDelayMilliseconds">復旧試行の最小間隔です。</param>
/// <param name="PollMilliseconds">監視の共通取得間隔です。</param>
public sealed record ProcessOptions(int? DefaultRecoveryAttempts = 3, int? DefaultReadyTimeoutMilliseconds = 30000, int RecoveryDelayMilliseconds = 1000, int PollMilliseconds = 250);
