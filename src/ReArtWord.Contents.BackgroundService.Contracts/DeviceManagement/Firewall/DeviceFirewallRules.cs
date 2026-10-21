using System.Globalization;
using System.Net;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;

/// <summary>OSへ渡すFirewall条件を有限の型とフィルターに限定します。</summary>
public static class DeviceFirewallRules
{
    private static readonly string[] profileResources = ["profile/Domain", "profile/Private", "profile/Public"];
    private static readonly string[] profileNames = ["Domain", "Private", "Public"];
    private static readonly string[] directions = ["Inbound", "Outbound"];
    private static readonly string[] actions = ["Allow", "Block"];
    private static readonly string[] protocols = ["Any", "TCP", "UDP", "ICMPv4", "ICMPv6"];
    private static readonly string[] portProtocols = ["TCP", "UDP"];
    private static readonly string[] addressKeywords = ["Any", "LocalSubnet"];
    private static string AnyValue => "Any";
    private static string RulePrefix => "rule/";

    /// <summary>追加・削除・有効無効の入力を区別します。</summary>
    public static void Validate(EDeviceAction action, string? resource, DeviceFirewallChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (action == EDeviceAction.FirewallProfile)
        {
            if (!profileResources.Contains(resource, StringComparer.Ordinal) || change.Enabled is null)
            {
                throw new ArgumentException("Device.InvalidFirewallProfile", nameof(change));
            }
            return;
        }
        if (resource is null || !resource.StartsWith(RulePrefix, StringComparison.Ordinal) || resource.Length <= 5)
        {
            throw new ArgumentException("Device.SelectFirewallRule", nameof(resource));
        }
        if (action == EDeviceAction.FirewallRuleEnabled && change.Enabled is null)
        {
            throw new ArgumentException("Device.InvalidFirewallRule", nameof(change));
        }
        if (action != EDeviceAction.FirewallRuleAdd)
        {
            return;
        }
        DeviceFirewallRule rule = change.Rule ?? throw new ArgumentException("Device.InvalidFirewallRule", nameof(change));
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Name);
        if (256 < rule.Name.Length || rule.Name.Any(char.IsControl)
            || !directions.Contains(rule.Direction, StringComparer.Ordinal) || !actions.Contains(rule.Action, StringComparer.Ordinal)
            || !protocols.Contains(rule.Protocol, StringComparer.Ordinal) || !Profiles(rule.Profile)
            || rule.Program.Length is 0 or > 4096 || rule.Program.Any(char.IsControl))
        {
            throw new ArgumentException("Device.InvalidFirewallRule", nameof(change));
        }
        Ports(rule.LocalPorts);
        Ports(rule.RemotePorts);
        Addresses(rule.LocalAddresses);
        Addresses(rule.RemoteAddresses);
        string any = AnyValue;
        if (!portProtocols.Contains(rule.Protocol, StringComparer.Ordinal) && (rule.LocalPorts.Any(port => !string.Equals(port, any, StringComparison.Ordinal))
            || rule.RemotePorts.Any(port => !string.Equals(port, any, StringComparison.Ordinal))))
        {
            throw new ArgumentException("Device.PortsRequireTcpOrUdp", nameof(change));
        }
    }

    /// <summary>プロファイルの集合を限定します。</summary>
    /// <returns>有効なプロファイルの組合せです。</returns>
    private static bool Profiles(string value)
    {
        if (string.Equals(value, AnyValue, StringComparison.Ordinal))
        {
            return true;
        }
        string[] profiles = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return profiles.Length is > 0 and <= 3 && profiles.All(profile => profileNames.Contains(profile, StringComparer.Ordinal))
            && profiles.Distinct(StringComparer.Ordinal).Count() == profiles.Length;
    }

    /// <summary>単一ポートと閉区間だけを受け付けます。</summary>
    private static void Ports(IReadOnlyList<string> values)
    {
        if (values.Count is 0 or > 64)
        {
            throw new ArgumentException("Device.InvalidPorts", nameof(values));
        }
        foreach (string value in values)
        {
            if (string.Equals(value, AnyValue, StringComparison.Ordinal))
            {
                continue;
            }
            string[] parts = value.Split('-');
            if (parts.Length is < 1 or > 2 || parts.Any(part => !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                || (parts.Length == 2 && ushort.Parse(parts[1], CultureInfo.InvariantCulture) < ushort.Parse(parts[0], CultureInfo.InvariantCulture)))
            {
                throw new ArgumentException("Device.InvalidPorts", nameof(values));
            }
        }
    }

    /// <summary>IP・CIDRと限定したOSキーワードを検証します。</summary>
    private static void Addresses(IReadOnlyList<string> values)
    {
        if (values.Count is 0 or > 64)
        {
            throw new ArgumentException("Device.InvalidAddress", nameof(values));
        }
        foreach (string value in values)
        {
            if (addressKeywords.Contains(value, StringComparer.Ordinal))
            {
                continue;
            }
            string[] parts = value.Split('/');
            if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out IPAddress? address))
            {
                throw new ArgumentException("Device.InvalidAddress", nameof(values));
            }
            int bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32
                                                                                              : 128;
            if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) || prefix < 0 || bits < prefix))
            {
                throw new ArgumentException("Device.InvalidAddress", nameof(values));
            }
        }
    }
}
