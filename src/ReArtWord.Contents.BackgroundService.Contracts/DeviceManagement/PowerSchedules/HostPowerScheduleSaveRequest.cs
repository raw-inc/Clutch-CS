using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>同じクラスタ・Hostの表示済み版に対する予定の置換要求です。</summary>
public sealed record HostPowerScheduleSaveRequest(Guid OperationId, string ClusterId, string HostId, string? ExpectedRevision, IReadOnlyList<HostPowerSchedule> Schedules,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] HostWakeProfile? WakeProfile = null);
