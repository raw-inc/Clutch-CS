namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>安定したInterfaceGuidとNICの現在設定です。</summary>
/// <param name="Id">InterfaceGuid。</param>
/// <param name="Name">OS上の表示名。</param>
/// <param name="Description">ドライバーの説明。</param>
/// <param name="Status">現在のリンク状態。</param>
/// <param name="MacAddress">MACアドレス。</param>
/// <param name="Physical">物理NICか。</param>
/// <param name="IPv4">IPv4の取得結果。</param>
/// <param name="IPv6">IPv6の取得結果。</param>
/// <param name="VirtualNicId">対応する管理OS用vNIC。</param>
/// <param name="SwitchId">対応を確認した仮想スイッチ。</param>
/// <param name="VlanId">確認したAccess VLAN。</param>
/// <param name="Revision">このNICの内容版。</param>
/// <param name="HyperV">OSデバイスIDまたは管理OS照会で確認したHyper-V NICか。</param>
/// <param name="VlanMode">確認できたVLANモード。nullは未確認。</param>
public sealed record DeviceNetworkInterface(string Id, string Name, string Description, string Status, string MacAddress, bool Physical,
    [property: System.Text.Json.Serialization.JsonPropertyName("ipv4")] DeviceIpConfiguration? IPv4,
    [property: System.Text.Json.Serialization.JsonPropertyName("ipv6")] DeviceIpConfiguration? IPv6, string? VirtualNicId = null, string? SwitchId = null, int? VlanId = null, string? Revision = null, bool HyperV = false, string? VlanMode = null);
