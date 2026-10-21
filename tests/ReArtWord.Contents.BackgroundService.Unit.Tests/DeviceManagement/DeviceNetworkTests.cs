using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>部分更新、一括候補、実設定との矛盾を同じ境界で検証します。</summary>
public sealed class DeviceNetworkTests
{
    /// <summary>IPv4/IPv6をprefix長に従って同じネットワークへまとめます。</summary>
    [Theory]
    [InlineData("10.0.10.121", 18, 4, "10.0.0.0/18")]
    [InlineData("2001:db8:abcd:1234::1", 56, 6, "2001:db8:abcd:1200::/56")]
    public void NormalizesNetworks(string address, int prefix, int family, string expected) => Assert.Equal(expected, DeviceNetworks.Subnet(address, prefix, family));

    /// <summary>同じ表記でもリンクを特定できないアドレスを候補にしません。</summary>
    [Theory]
    [InlineData("169.254.1.2", 4)]
    [InlineData("127.1.2.3", 4)]
    [InlineData("224.0.0.1", 4)]
    [InlineData("fe80::1", 6)]
    [InlineData("::1", 6)]
    [InlineData("::", 6)]
    public void ExcludesAmbiguousNetworks(string address, int family) => Assert.False(DeviceNetworks.CanGroup(address, family));

    /// <summary>省略・scope・基数の曖昧な入力はOSへ渡しません。</summary>
    [Theory]
    [InlineData("127.1", 4)]
    [InlineData("010.0.0.1", 4)]
    [InlineData("0x7f.0.0.1", 4)]
    [InlineData("fe80::1%12", 6)]
    public void RejectsAmbiguousAddresses(string address, int family) => Assert.Throws<ArgumentException>(() => DeviceNetworks.Address(address, family));

    /// <summary>DNS変更でDHCPを解除せず、ゲートウェイ変更では明示指定を要求します。</summary>
    [Fact]
    public void GatewayAndStaticAddressChangesRequireExplicitAddressing()
    {
        DeviceNetworkInterface current = Interface(true);
        DeviceNetworks.ValidateCurrent(new(4, AutomaticDns: false, Dns: ["10.0.0.53"]), current);
        Assert.Throws<ArgumentException>(() => DeviceNetworks.ValidateCurrent(new(4, Gateways: ["10.0.0.1"]), current));
        Assert.Throws<ArgumentException>(() => DeviceNetworks.ValidateCurrent(new(4, Addresses: [new("10.0.0.2", 24)]), current));
        DeviceNetworks.ValidateCurrent(new(4, Dhcp: false, Gateways: ["10.0.0.1"]), current);
        Assert.Throws<ArgumentException>(() => DeviceNetworks.ValidateCurrent(new(4, Dhcp: false, Gateways: ["10.1.0.1"]), current));
        Assert.Throws<ArgumentException>(() => DeviceNetworks.ValidateCurrent(new(6, Dhcp: false, Gateways: ["fe80::1"]), current));
        DeviceNetworks.ValidateCurrent(new(6, Dhcp: false, RouterDiscovery: false, Gateways: ["fe80::1"]), current);
    }

    /// <summary>DNSの優先順位の逆転を同じ結果として承認しません。</summary>
    [Fact]
    public void ReadBackChecksDnsOrderAndIgnoresUnchangedFields()
    {
        DeviceNetworkInterface current = Interface(true);
        DeviceNetworkChange change = new(4, AutomaticDns: false, Dns: ["10.0.0.53", "10.0.0.54"]);
        DeviceChangeRequest request = new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.Network, new(EDeviceArea.Network, current.Id),
            new string('a', 64), JsonSerializer.SerializeToElement(change, Contracts.Serialization.ContractJson.Options), true);
        DeviceNetworkInterface applied = current with { IPv4 = current.IPv4! with { AutomaticDns = false, Dns = change.Dns! } };
        Assert.True(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(applied)));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(applied with { IPv4 = applied.IPv4! with { Dns = change.Dns!.Reverse().ToArray() } })));
    }

    /// <summary>DHCPとRAが有効な比較用IFを返します。</summary>
    /// <returns>設定変更前の単一IFです。</returns>
    private static DeviceNetworkInterface Interface(bool dhcp) => new(Guid.NewGuid().ToString("D"), "LAN", "NIC", "Up", "001122334455", true,
        new(true, dhcp, true, 25, true, [new("10.0.0.2", 24)], [], []), new(true, dhcp, true, 25, true, [new("2001:db8::2", 64)], [], [], true));
}
