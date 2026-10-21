using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>一つの予定について次回と直近の受付を表示します。</summary>
public sealed record HostPowerScheduleStatus(Guid Id, DateTimeOffset? NextAt, DateTimeOffset? LastAt = null, EOutcome? Outcome = null, string? Reason = null);
