namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>電源予定を保持するクラスタと対象Hostです。</summary>
public sealed record HostPowerScheduleReadRequest(string ClusterId, string HostId);
