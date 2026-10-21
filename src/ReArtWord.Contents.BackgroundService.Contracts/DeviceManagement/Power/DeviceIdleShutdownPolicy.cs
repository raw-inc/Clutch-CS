namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;

/// <summary>PC全体で共有する無操作シャットダウン設定。0は無効です。</summary>
public sealed record DeviceIdleShutdownPolicy(uint AcSeconds = 0, uint DcSeconds = 0, Guid Version = default, DateTimeOffset EnabledAt = default);
