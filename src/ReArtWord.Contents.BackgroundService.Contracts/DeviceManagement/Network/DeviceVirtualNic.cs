namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>Hyper-Vの管理OSが所有する仮想NICです。</summary>
/// <param name="Id">Hyper-Vの固定識別子。</param>
/// <param name="Name">仮想NICの名前。</param>
/// <param name="SwitchId">接続スイッチの識別子。</param>
/// <param name="SwitchName">接続スイッチの表示名。</param>
/// <param name="MacAddress">対応付けに使うMACアドレス。</param>
/// <param name="VlanMode">確認したVLANモード。</param>
/// <param name="VlanId">AccessモードのVLAN。</param>
/// <param name="Revision">このvNICの内容版。</param>
public sealed record DeviceVirtualNic(string Id, string Name, string SwitchId, string SwitchName, string MacAddress, string VlanMode, int? VlanId, string? Revision = null);
