namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>現在の予定と、その回の受付結果です。OS停止・起動完了とは区別します。</summary>
public sealed record HostPowerScheduleView(string ClusterId, string HostId, string? Revision, IReadOnlyList<HostPowerSchedule> Schedules,
    IReadOnlyList<HostPowerScheduleStatus> Status, bool WakeProfileAvailable, bool Active);
