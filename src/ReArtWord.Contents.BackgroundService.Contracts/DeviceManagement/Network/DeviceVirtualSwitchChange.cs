namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>仮想スイッチの作成条件です。削除時は全項目を未指定にします。</summary>
/// <param name="Name">作成名。</param>
/// <param name="Kind">External、Internal、Private。</param>
/// <param name="PhysicalInterfaceId">Externalの物理NICのInterfaceGuid。</param>
/// <param name="AllowManagementOS">Externalの管理OS共有有無。</param>
public sealed record DeviceVirtualSwitchChange(string? Name = null, string? Kind = null, string? PhysicalInterfaceId = null, bool? AllowManagementOS = null);
