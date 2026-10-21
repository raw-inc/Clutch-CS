using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>予定の変更時刻と固定参加者を、予定と一緒に共有します。</summary>
internal sealed record HostPowerScheduleRecord(string HostId, IReadOnlyList<HostPowerSchedule> Schedules, DateTimeOffset ChangedAt, Guid OperationId, IReadOnlyList<PeerInfo> Members);
