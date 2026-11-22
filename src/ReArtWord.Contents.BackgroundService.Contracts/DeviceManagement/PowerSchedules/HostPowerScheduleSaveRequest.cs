using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>同じクラスタ・Hostの表示済み版に対する予定の置換要求です。</summary>
public sealed record HostPowerScheduleSaveRequest(Guid OperationId, string ClusterId, string HostId, string? ExpectedRevision, IReadOnlyList<HostPowerSchedule> Schedules,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] HostWakeProfile? WakeProfile = null)
{
    /// <summary>初回に選んだ派生IDの生成方式です。未指定の旧要求は旧方式のまま扱います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
