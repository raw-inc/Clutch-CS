namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>全参加者への設定保存が成功した後だけ作る、保持期限のない稼働基準です。</summary>
internal sealed record HostPowerScheduleActivation(Guid OperationId, string ValueHash);
