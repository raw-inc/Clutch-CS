namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>PIDの再利用を別プロセスとして識別する情報です。</summary>
/// <param name="ProcessId">OSが発行したPIDです。</param>
/// <param name="StartedAt">OSが報告した起動時刻です。</param>
public sealed record ProcessIdentity(int ProcessId, DateTimeOffset StartedAt);
