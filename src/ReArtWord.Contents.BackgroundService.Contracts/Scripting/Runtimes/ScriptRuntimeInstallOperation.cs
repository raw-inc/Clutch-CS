using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

/// <summary>導入受付と完了を分け、実配置先・既定値反映・中断を追跡します。</summary>
public sealed record ScriptRuntimeInstallOperation(ScriptRuntimeInstallRequest Request, string Fingerprint, string Phase, EOutcome Outcome, bool Completed,
    DateTimeOffset UpdatedAt, string? UserName = null, string? Directory = null, string? ExecutablePath = null, bool DefaultApplied = false,
    long DownloadedBytes = 0, long? TotalBytes = null, string? Reason = null);
