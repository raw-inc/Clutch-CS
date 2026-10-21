namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;

/// <summary>現在の電源プラン、OS設定とClutch-CSによる無操作停止設定です。</summary>
public sealed record DevicePowerState(Guid SchemeId, string SchemeName, DevicePowerValues Ac, DevicePowerValues Dc,
    bool? FastStartup, bool SleepAvailable, bool HibernateAvailable, DeviceIdleShutdownPolicy IdleShutdown, IReadOnlyList<string> Restrictions);
