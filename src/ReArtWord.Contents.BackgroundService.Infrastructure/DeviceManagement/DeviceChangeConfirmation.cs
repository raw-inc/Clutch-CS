using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>コマンドの終了だけでなく、変更した項目の実際の再読取りを照合します。</summary>
internal static class DeviceChangeConfirmation
{
    /// <summary>操作種類が要求した値だけを実取得値と比較します。</summary>
    /// <returns>要求した反映を確認できたかです。</returns>
    internal static bool Matches(DeviceChangeRequest request, DeviceSnapshot after)
    {
        switch (request.Action)
        {
            case EDeviceAction.WakeOnLan:
                return DeviceSnapshots.Read<Contracts.DeviceManagement.WakeOnLan.DeviceWakeSelection>(request.Change)
                    .Matches(DeviceSnapshots.Read<Contracts.DeviceManagement.WakeOnLan.DeviceWakeState>(after.Data).Selection);
            case EDeviceAction.Login:
                return DeviceSnapshots.Read<Contracts.DeviceManagement.Login.DeviceLoginChange>(request.Change).Matches(DeviceSnapshots.Read<Contracts.DeviceManagement.Login.DeviceLoginState>(after.Data));
            case EDeviceAction.WindowsUpdate:
                return DeviceSnapshots.Read<Contracts.DeviceManagement.WindowsUpdate.DeviceWindowsUpdateChange>(request.Change).Matches(DeviceSnapshots.Read<Contracts.DeviceManagement.WindowsUpdate.DeviceWindowsUpdateState>(after.Data));
            case EDeviceAction.Power:
                return DeviceSnapshots.Read<Contracts.DeviceManagement.Power.DevicePowerChange>(request.Change).Matches(DeviceSnapshots.Read<Contracts.DeviceManagement.Power.DevicePowerState>(after.Data));
            case EDeviceAction.Display:
                return DeviceSnapshots.Read<DeviceDisplayInfo>(after.Data).Current == DeviceSnapshots.Read<DeviceDisplayChange>(request.Change).Mode;
            case EDeviceAction.Hosts:
                return string.Equals(Lines(DeviceSnapshots.Read<DeviceHostsChange>(request.Change).Content), Lines(DeviceSnapshots.Read<DeviceHostsState>(after.Data).Content), StringComparison.Ordinal);
            case EDeviceAction.Network:
                return Network(DeviceSnapshots.Read<DeviceNetworkChange>(request.Change), DeviceSnapshots.Read<DeviceNetworkInterface>(after.Data));
            case EDeviceAction.NetworkProtocols:
                DeviceNetworkProtocols protocols = DeviceSnapshots.Read<DeviceNetworkProtocols>(request.Change);
                DeviceNetworkInterface nic = DeviceSnapshots.Read<DeviceNetworkInterface>(after.Data);
                return (protocols.IPv4Enabled is null || protocols.IPv4Enabled == nic.IPv4?.Enabled)
                    && (protocols.IPv6Enabled is null || protocols.IPv6Enabled == nic.IPv6?.Enabled);
            case EDeviceAction.Environment:
                DeviceEnvironmentChange environment = DeviceSnapshots.Read<DeviceEnvironmentChange>(request.Change);
                if (environment.Delete)
                {
                    return after.Data.ValueKind == JsonValueKind.Null;
                }
                DeviceEnvironmentVariable variable = DeviceSnapshots.Read<DeviceEnvironmentVariable>(after.Data);
                return string.Equals(variable.Value, environment.Value, StringComparison.Ordinal) && variable.Expandable == environment.Expandable;
            case EDeviceAction.FirewallProfile:
                return DeviceSnapshots.Read<DeviceFirewallProfile>(after.Data).Enabled == DeviceSnapshots.Read<DeviceFirewallChange>(request.Change).Enabled;
            case EDeviceAction.FirewallRuleEnabled:
                return DeviceSnapshots.Read<DeviceFirewallRule>(after.Data).Enabled == DeviceSnapshots.Read<DeviceFirewallChange>(request.Change).Enabled;
            case EDeviceAction.FirewallRuleRemove:
            case EDeviceAction.VirtualNicDelete:
            case EDeviceAction.VirtualSwitchDelete:
                return after.Data.ValueKind == JsonValueKind.Null;
            case EDeviceAction.FirewallRuleAdd:
                return Firewall(DeviceSnapshots.Read<DeviceFirewallChange>(request.Change).Rule!, DeviceSnapshots.Read<DeviceFirewallRule>(after.Data));
            case EDeviceAction.VirtualNicVlan:
                return VirtualNic(DeviceSnapshots.Read<DeviceVirtualNicChange>(request.Change), DeviceSnapshots.Read<DeviceVirtualNic>(after.Data));
            case EDeviceAction.VirtualNicCreate:
                DeviceVirtualNicChange created = DeviceSnapshots.Read<DeviceVirtualNicChange>(request.Change);
                DeviceVirtualNic[] matching = DeviceSnapshots.Read<DeviceNetworkState>(after.Data).VirtualNics.Where(nic => string.Equals(nic.Name, created.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(nic.SwitchId, created.SwitchId, StringComparison.OrdinalIgnoreCase)).ToArray();
                return matching.Length == 1 && VirtualNic(created, matching[0]);
            case EDeviceAction.VirtualSwitchCreate:
                return VirtualSwitch(DeviceSnapshots.Read<DeviceVirtualSwitchChange>(request.Change), DeviceSnapshots.Read<DeviceVirtualSwitchState>(after.Data));
            default:
                return false;
        }
    }

    /// <summary>同名だけでなく種別・物理NIC・管理OS共有を再照合します。</summary>
    /// <returns>要求したスイッチが一つだけ存在するかです。</returns>
    private static bool VirtualSwitch(DeviceVirtualSwitchChange wanted, DeviceVirtualSwitchState state)
    {
        DeviceVirtualSwitchDetails[] found = state.Switches.Where(item => string.Equals(item.Switch.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length != 1 || !string.Equals(found[0].Switch.Kind, wanted.Kind, StringComparison.Ordinal))
        {
            return false;
        }
        DeviceVirtualSwitchDetails actual = found[0];
        bool shared = wanted.AllowManagementOS ?? string.Equals(wanted.Kind, "Internal", StringComparison.Ordinal);
        if (actual.AllowManagementOS != shared)
        {
            return false;
        }
        if (wanted.PhysicalInterfaceId is null)
        {
            return actual.PhysicalInterfaceIds.Count == 0;
        }
        return actual.PhysicalInterfaceIds.Count == 1 && string.Equals(actual.PhysicalInterfaceIds[0], wanted.PhysicalInterfaceId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>通信方式を変えない部分変更も、指定された値は省略せず確認します。</summary>
    /// <returns>NICの反映確認です。</returns>
    private static bool Network(DeviceNetworkChange change, DeviceNetworkInterface current)
    {
        DeviceIpConfiguration? value = change.Family == 4 ? current.IPv4
                                                          : current.IPv6;
        bool clearDns = change.AutomaticDns == false && change.Dns is { Count: 0 };
        if (value is null || (change.Enabled is not null && change.Enabled != value.Enabled) || (change.Dhcp is not null && change.Dhcp != value.Dhcp)
            || (change.AutomaticMetric is not null && change.AutomaticMetric != value.AutomaticMetric) || (change.Metric is not null && change.Metric != value.Metric)
            || (!clearDns && change.AutomaticDns is not null && change.AutomaticDns != value.AutomaticDns)
            || (change.RouterDiscovery is not null && change.RouterDiscovery != value.RouterDiscovery))
        {
            return false;
        }
        if (change.Addresses is not null && !Same(change.Addresses.Select(AddressKey), value.Addresses.Where(address => !address.Automatic).Select(AddressKey)))
        {
            return false;
        }
        bool dnsMatches = change.Dns is null || change.Dns.Select(Address).SequenceEqual(value.Dns.Select(Address), StringComparer.OrdinalIgnoreCase);
        if (clearDns && value.AutomaticDns == true && change.Family == 6)
        {
            // Windows reports these built-in discovery addresses even after netsh clears all configured IPv6 DNS servers.
            string[] defaults = ["fec0:0:0:ffff::1", "fec0:0:0:ffff::2", "fec0:0:0:ffff::3"];
            dnsMatches |= value.Dns.Select(Address).SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase);
        }
        return dnsMatches
            && (change.Gateways is null || Same(change.Gateways.Select(Address), value.Gateways.Select(Address)));
    }

    /// <summary>追加したルールの基本条件を再取得したフィルターと照合します。</summary>
    /// <returns>ルールの反映確認です。</returns>
    private static bool Firewall(DeviceFirewallRule wanted, DeviceFirewallRule actual) => actual.Editable
        && string.Equals(wanted.Name, actual.Name, StringComparison.Ordinal) && string.Equals(wanted.Direction, actual.Direction, StringComparison.OrdinalIgnoreCase)
        && string.Equals(wanted.Action, actual.Action, StringComparison.OrdinalIgnoreCase) && wanted.Enabled == actual.Enabled
        && Same(wanted.Profile.Split(','), actual.Profile.Split(',')) && string.Equals(wanted.Protocol, actual.Protocol, StringComparison.OrdinalIgnoreCase)
        && Same(wanted.LocalPorts, actual.LocalPorts) && Same(wanted.RemotePorts, actual.RemotePorts)
        && Same(wanted.LocalAddresses, actual.LocalAddresses) && Same(wanted.RemoteAddresses, actual.RemoteAddresses)
        && string.Equals(wanted.Program, actual.Program, StringComparison.OrdinalIgnoreCase);

    /// <summary>未対応モードをAccessやタグなしと推測しません。</summary>
    /// <returns>選択したVLAN設定の一致です。</returns>
    private static bool VirtualNic(DeviceVirtualNicChange wanted, DeviceVirtualNic actual)
    {
        string mode = wanted.VlanId is null ? "Untagged"
                                            : "Access";
        return string.Equals(mode, actual.VlanMode, StringComparison.OrdinalIgnoreCase) && (wanted.VlanId is null || wanted.VlanId == actual.VlanId);
    }

    /// <summary>順序に意味を持たないフィルター集合を比較します。</summary>
    /// <returns>同じ値の集合かです。</returns>
    private static bool Same(IEnumerable<string> first, IEnumerable<string> second) => first.Select(value => value.Trim()).Order(StringComparer.OrdinalIgnoreCase)
        .SequenceEqual(second.Select(value => value.Trim()).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>OSが正規化したIPの表記を比較可能にします。</summary>
    /// <returns>正規化されたIP文字列です。</returns>
    private static string Address(string value) => IPAddress.Parse(value.Split('%')[0]).ToString();

    /// <summary>prefixを含む固定アドレスの比較キーです。</summary>
    /// <returns>アドレスとprefixの組です。</returns>
    private static string AddressKey(DeviceIpAddress address) => Address(address.Address) + "/" + address.PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>hostsで保持した改行形式の違いだけを除きます。</summary>
    /// <returns>比較用の本文です。</returns>
    private static string Lines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
