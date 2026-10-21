using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>受理済みの回を予定ごとの現在値として保持し、毎日の行を増やしません。</summary>
internal sealed record HostPowerScheduleOccurrence(Guid ScheduleId, DateTimeOffset DueAt, Guid OperationId, EOutcome Outcome, string? Reason);
