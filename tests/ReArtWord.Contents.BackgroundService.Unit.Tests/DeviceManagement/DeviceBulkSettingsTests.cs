using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>複数ホストの条件再現と、DNS未設定を要求する境界を確認します。</summary>
public sealed class DeviceBulkSettingsTests
{
    /// <summary>詳細を省いたルールを複製可能と扱わず、IDをワイルドカード展開しません。</summary>
    /// <returns>実PowerShellでのJSON配列とOS条件の照合です。</returns>
    [WindowsTheory]
    [InlineData("basic", true)]
    [InlineData("service", false)]
    [InlineData("package", false)]
    [InlineData("security", false)]
    [InlineData("icmp", false)]
    [InlineData("readonly", false)]
    public async Task FirewallTemplatesRequireReproducibleConditionsAsync(string scenario, bool cloneable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("FirewallTemplateBoundary", new { scenario }).ConfigureAwait(true);
        DeviceFirewallTemplates templates = DeviceSnapshots.Read<DeviceFirewallTemplates>(result);
        Assert.Equal(2, templates.Rules.Count);
        Assert.Contains(templates.Rules, item => string.Equals(item.Rule.Id, "literal[*]", StringComparison.Ordinal));
        Assert.All(templates.Rules, item => Assert.Equal(cloneable, item.Cloneable));
    }

    /// <summary>DNSなしを自動取得への復帰と別のOS操作にし、選択したfamilyだけへ渡します。</summary>
    /// <returns>実NICを変更しない呼出境界の確認です。</returns>
    [WindowsTheory]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(4, true)]
    public async Task ClearsDnsOnTheSelectedInterfaceAndFamilyAsync(int family, bool fail)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DeviceNetworkChange change = new(family, AutomaticDns: false, Dns: []);
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("NetworkBoundary", new { id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", name = "literal[*]", change, fail }).ConfigureAwait(true);
        Assert.Equal(!fail, result.GetProperty("success").GetBoolean());
        JsonElement call = Assert.Single(result.GetProperty("calls").EnumerateArray());
        Assert.Equal("dns-none", call.GetProperty("kind").GetString());
        Assert.Equal(42, call.GetProperty("index").GetInt32());
        Assert.Equal(family, call.GetProperty("family").GetInt32());
    }

    /// <summary>未設定に自動DNSが再投入される組合せは開始前に拒否します。</summary>
    [Fact]
    public void EmptyDnsRequiresExplicitManualAddressingAndVerifiesRealServers()
    {
        DeviceIpConfiguration configuration = new(true, false, true, 10, true, [new("192.0.2.2", 24)], [], []);
        DeviceNetworkInterface current = new(Guid.NewGuid().ToString(), "Ethernet", "Adapter", "Up", "00", true, configuration, configuration with { RouterDiscovery = false });
        DeviceNetworkChange change = new(4, AutomaticDns: false, Dns: [], Gateways: ["192.0.2.1"]);
        DeviceNetworks.ValidateCurrent(change, current);
        Assert.Throws<ArgumentException>(() => DeviceNetworks.ValidateCurrent(change, current with { IPv4 = configuration with { Dhcp = true } }));
        DeviceChangeRequest request = new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.Network, new(EDeviceArea.Network, current.Id), new string('a', 64), DeviceSnapshots.Create(change).Data, true);
        Assert.True(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(current with { IPv4 = configuration with { Gateways = change.Gateways! } })));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(current with { IPv4 = configuration with { Gateways = change.Gateways!, Dns = ["192.0.2.53"] } })));
        DeviceNetworkChange ipv6 = new(6, AutomaticDns: false, Dns: []);
        request = request with { Change = DeviceSnapshots.Create(ipv6).Data };
        DeviceIpConfiguration defaults = configuration with { RouterDiscovery = false, Dns = ["fec0:0:0:ffff::1", "fec0:0:0:ffff::2", "fec0:0:0:ffff::3"] };
        Assert.True(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(current with { IPv6 = defaults })));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(current with { IPv6 = defaults with { AutomaticDns = false } })));
        Assert.Throws<ArgumentException>(() => DeviceNetworks.ValidateCurrent(ipv6, current with { IPv6 = defaults with { RouterDiscovery = true } }));
    }

    /// <summary>通常検索の上限は変えず、固定IDの一括読取りだけに有限のJSON配列を許します。</summary>
    [Fact]
    public void BoundsBulkRuleReadsSeparatelyFromSearchFilters()
    {
        DeviceChangeRules.ValidateRead(new(EDeviceArea.Firewall, "bulk", Filter: new string('a', 8192)));
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.ValidateRead(new(EDeviceArea.Firewall, "bulk", Filter: new string('a', 8193))));
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.ValidateRead(new(EDeviceArea.Firewall, Filter: new string('a', 129))));
    }
}
