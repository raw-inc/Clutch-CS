namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>既存のHyper-V仮想スイッチです。</summary>
/// <param name="Id">スイッチの識別子。</param>
/// <param name="Name">表示名。</param>
/// <param name="Kind">External・Internal・Private等。</param>
/// <param name="PhysicalInterface">接続された物理NICの説明。</param>
public sealed record DeviceVirtualSwitch(string Id, string Name, string Kind, string? PhysicalInterface = null);
