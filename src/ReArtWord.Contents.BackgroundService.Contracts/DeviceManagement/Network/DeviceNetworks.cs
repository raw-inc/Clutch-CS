using System.Net;
using System.Net.Sockets;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>単一NICと一括候補のIP範囲を同じ規則で検証します。</summary>
public static class DeviceNetworks
{
    /// <summary>省略IPv4や別ファミリーを暗黙に読み替えません。</summary>
    /// <returns>指定ファミリーのIPアドレスです。</returns>
    public static IPAddress Address(string value, int family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (family is not (4 or 6) || value.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(value, out IPAddress? address)
            || (family == 4 && (address.AddressFamily != AddressFamily.InterNetwork || value.Split('.').Length != 4
                || value.Split('.').Any(part => part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || (1 < part.Length && part[0] == '0'))))
            || (family == 6 && address.AddressFamily != AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("Device.InvalidAddress", nameof(value));
        }
        return address;
    }

    /// <summary>prefix長で正規化し、表示用の同一サブネット候補を作ります。</summary>
    /// <returns>network/prefix表現です。</returns>
    public static string Subnet(string value, int prefix, int family)
    {
        IPAddress address = Address(value, family);
        int bits = family == 4 ? 32
                               : 128;
        ArgumentOutOfRangeException.ThrowIfNegative(prefix);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefix, bits);
        byte[] bytes = address.GetAddressBytes();
        for (int index = 0; index < bytes.Length; index++)
        {
            int remaining = Math.Clamp(prefix - index * 8, 0, 8);
            bytes[index] &= (byte)(255 << (8 - remaining));
        }
        return new IPAddress(bytes) + "/" + prefix.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>別リンクでも共通になるアドレスを自動一括候補へ入れません。</summary>
    /// <returns>利用者が確認するサブネット候補に使えるかです。</returns>
    public static bool CanGroup(string value, int family)
    {
        IPAddress address = Address(value, family);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        return family != 4 || (bytes[0] is < 224 && !(bytes[0] == 169 && bytes[1] == 254));
    }

    /// <summary>指定された項目だけを変更する組合せを確認します。</summary>
    public static void Validate(DeviceNetworkChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Family is not (4 or 6) || change.Metric is < 0 or > 9999 || (change.AutomaticMetric != false && change.Metric is not null)
            || (change.AutomaticMetric == false && change.Metric is null) || (change.AutomaticDns == true && change.Dns is { Count: > 0 })
            || (change.AutomaticDns == false && change.Dns is null) || (change.Dns is not null && change.AutomaticDns is null)
            || (change.Dhcp == true && (change.Addresses is { Count: > 0 } || change.Gateways is { Count: > 0 })) || (change.Family == 4 && change.RouterDiscovery is not null))
        {
            throw new ArgumentException("Device.InvalidNetworkChange", nameof(change));
        }
        if (change.Enabled is null && change.Dhcp is null && change.AutomaticMetric is null && change.Metric is null && change.Addresses is null
            && change.AutomaticDns is null && change.Dns is null && change.Gateways is null && change.RouterDiscovery is null)
        {
            throw new ArgumentException("Device.NoChanges", nameof(change));
        }
        if (change.Addresses is { Count: > 32 } || change.Dns is { Count: > 16 } || change.Gateways is { Count: > 8 })
        {
            throw new ArgumentException("Device.TooManyValues", nameof(change));
        }
        foreach (DeviceIpAddress address in change.Addresses ?? [])
        {
            _ = Subnet(address.Address, address.PrefixLength, change.Family);
            if (address.Automatic)
            {
                throw new ArgumentException("Device.ManualAddressesRequired", nameof(change));
            }
        }
        foreach (string value in (change.Dns ?? []).Concat(change.Gateways ?? []))
        {
            _ = Address(value, change.Family);
        }
    }

    /// <summary>DHCPを黙って解除せず、明示ゲートウェイがNICの範囲へ属するか確認します。</summary>
    public static void ValidateCurrent(DeviceNetworkChange change, DeviceNetworkInterface current)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(current);
        Validate(change);
        DeviceIpConfiguration? configuration = change.Family == 4 ? current.IPv4
                                                                  : current.IPv6;
        if (change.AutomaticDns == false && change.Dns is { Count: 0 }
            && ((change.Dhcp ?? configuration?.Dhcp) != false || (change.Family == 6 && (change.RouterDiscovery ?? configuration?.RouterDiscovery) != false)))
        {
            throw new ArgumentException("Device.EmptyDnsRequiresManualAddressing", nameof(change));
        }
        if (change.Addresses is not null && (change.Dhcp ?? configuration?.Dhcp) != false)
        {
            throw new ArgumentException("Device.AddressManagedByDhcp", nameof(change));
        }
        if (change.Gateways is not null && (change.Dhcp ?? configuration?.Dhcp) != false)
        {
            throw new ArgumentException("Device.GatewayManagedByDhcp", nameof(change));
        }
        if (change.Gateways is not null && change.Family == 6 && (change.RouterDiscovery ?? configuration?.RouterDiscovery) != false)
        {
            throw new ArgumentException("Device.GatewayManagedByRouterDiscovery", nameof(change));
        }
        foreach (string gateway in change.Gateways ?? [])
        {
            IPAddress address = Address(gateway, change.Family);
            if (change.Family == 6 && address.IsIPv6LinkLocal)
            {
                continue;
            }
            if (!(change.Addresses ?? configuration?.Addresses ?? []).Any(item => string.Equals(Subnet(item.Address.Split('%')[0], item.PrefixLength, change.Family),
                    Subnet(gateway, item.PrefixLength, change.Family), StringComparison.Ordinal)))
            {
                throw new ArgumentException("Device.GatewayOutsideSubnet", nameof(change));
            }
        }
    }
}
