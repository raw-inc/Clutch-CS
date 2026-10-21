namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>既存スイッチに接続するvNICの作成またはVLAN変更です。</summary>
/// <param name="Name">作成する名前。</param>
/// <param name="SwitchId">選択済みスイッチ。</param>
/// <param name="VlanId">Access VLAN。nullはタグなし。</param>
public sealed record DeviceVirtualNicChange(string? Name = null, string? SwitchId = null, int? VlanId = null);
