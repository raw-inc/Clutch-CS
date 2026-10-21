namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>同じNICのIPv4/IPv6 bindingを、一つの内容版で確認して変更します。nullは未変更です。</summary>
public sealed record DeviceNetworkProtocols(
    [property: System.Text.Json.Serialization.JsonPropertyName("ipv4Enabled")] bool? IPv4Enabled = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("ipv6Enabled")] bool? IPv6Enabled = null)
{
    /// <summary>空の変更と存在しないbindingをOS操作前に拒否します。</summary>
    public void Validate(DeviceNetworkInterface? current = null)
    {
        if (IPv4Enabled is null && IPv6Enabled is null)
        {
            throw new ArgumentException("Device.NoChanges");
        }
        if (current is not null && ((IPv4Enabled is not null && current.IPv4 is null) || (IPv6Enabled is not null && current.IPv6 is null)))
        {
            throw new ArgumentException("Device.InterfaceUnavailable");
        }
    }
}
