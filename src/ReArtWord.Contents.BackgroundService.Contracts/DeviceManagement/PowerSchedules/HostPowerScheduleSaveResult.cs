using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>保存の確認結果と、確認できた現在版です。</summary>
public sealed record HostPowerScheduleSaveResult(EOutcome Outcome, string? Reason, HostPowerScheduleView? View = null);
