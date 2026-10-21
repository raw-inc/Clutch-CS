namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>OS別の対応範囲と、Coreが使う実行権限を示します。</summary>
/// <param name="OperatingSystem">OSの名前。</param>
/// <param name="MachineName">OSが返すPC名。</param>
/// <param name="Elevated">管理権限で実行しているか。</param>
/// <param name="Areas">提供できる設定領域。</param>
/// <param name="Users">明示選択できる読込み済みユーザー。</param>
public sealed record DeviceCapabilities(string OperatingSystem, string MachineName, bool Elevated, IReadOnlyList<EDeviceArea> Areas, IReadOnlyList<DeviceUserProfile> Users);
