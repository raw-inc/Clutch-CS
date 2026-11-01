using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>ブラウザーの疎な変更、両familyの一括反映、途中失敗と内容版を検証します。</summary>
public sealed class DeviceProtocolTests
{
    /// <summary>CIMの数値enumとgatewayが存在しない構成を従来の表示契約へ写します。</summary>
    /// <returns>Hyper-V読取りの並行完了を含む、実PowerShellの結果です。</returns>
    [WindowsFact]
    public async Task CimReadsPreserveBothFamiliesAndPermitNoDefaultRouteAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("NetworkReadBoundary", new { }).ConfigureAwait(true);
        DeviceNetworkInterface nic = Assert.Single(DeviceSnapshots.Read<DeviceNetworkState>(result).Interfaces);
        Assert.True(nic.IPv4!.Enabled);
        Assert.False(nic.IPv6!.Enabled);
        Assert.False(nic.IPv4.Dhcp);
        Assert.True(nic.IPv6.Dhcp);
        Assert.True(nic.IPv6.RouterDiscovery);
        Assert.True(nic.IPv4.AutomaticMetric);
        Assert.False(nic.IPv6.AutomaticMetric);
        Assert.False(Assert.Single(nic.IPv4.Addresses).Automatic);
        Assert.Equal("fe80::4", Assert.Single(nic.IPv6.Addresses).Address);
        Assert.True(Assert.Single(nic.IPv6.Addresses).Automatic);
        Assert.Empty(nic.IPv4.Gateways);
        Assert.Empty(nic.IPv6.Gateways);
    }

    /// <summary>省略項目を持つ実際のJSONでも、StrictModeで指定bindingだけを変更します。</summary>
    /// <returns>未指定のDHCP・DNS・metricを触らない確認です。</returns>
    [WindowsTheory, InlineData(4), InlineData(6)]
    public async Task SparseBrowserPatchDisablesOnlyTheRequestedProtocolAsync(int family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("NetworkBoundary", new
        {
            id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", name = "vEthernet [target]", change = new { family, enabled = false }, fail = false,
        }).ConfigureAwait(true);
        Assert.True(result.GetProperty("success").GetBoolean());
        JsonElement call = Assert.Single(result.GetProperty("calls").EnumerateArray());
        Assert.Equal(family == 4 ? "ms_tcpip"
                                 : "ms_tcpip6", call.GetProperty("component").GetString());
        Assert.False(call.GetProperty("enabled").GetBoolean());
    }

    /// <summary>両bindingの存在確認を先に行い、有効化を先行させ、途中失敗は成功にしません。</summary>
    /// <returns>OS境界の順序とpartial failureの記録です。</returns>
    [WindowsTheory, InlineData(false, false), InlineData(true, false), InlineData(false, true)]
    public async Task CombinedProtocolsValidateBothBeforeChangingAndPreservePartialFailureAsync(bool fail, bool missingV6)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("NetworkBoundary", new
        {
            id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", name = "vEthernet [target]", action = "networkProtocols",
            change = new { ipv4Enabled = true, ipv6Enabled = false }, initialV4 = false, fail, missingV6,
        }).ConfigureAwait(true);
        Assert.Equal(!fail && !missingV6, result.GetProperty("success").GetBoolean());
        Assert.Equal(!missingV6, result.GetProperty("started").GetBoolean());
        JsonElement[] calls = result.GetProperty("calls").EnumerateArray().ToArray();
        if (missingV6)
        {
            Assert.Empty(calls);
            return;
        }
        Assert.Equal(2, calls.Length);
        Assert.Equal("ms_tcpip", calls[0].GetProperty("component").GetString());
        Assert.True(calls[0].GetProperty("enabled").GetBoolean());
        Assert.False(calls[1].GetProperty("enabled").GetBoolean());
        Assert.Equal(fail ? 1
                          : 2, result.GetProperty("steps").GetArrayLength());
    }

    /// <summary>両方を一つの内容版で扱い、指定されていないfamilyは比較対象にしません。</summary>
    [Fact]
    public void CombinedContractRequiresAChangeAndConfirmsEveryRequestedBinding()
    {
        DeviceIpConfiguration ip = new(true, null, null, null, null, [], [], []);
        DeviceNetworkInterface nic = new(Guid.NewGuid().ToString(), "NIC", "NIC", "Up", "00", true, ip, ip);
        DeviceChangeRequest request = new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.NetworkProtocols,
            new(EDeviceArea.Network, nic.Id), new string('a', 64), DeviceSnapshots.Create(new DeviceNetworkProtocols(false, false)).Data, true);
        DeviceChangeRules.Validate(request);
        Assert.True(request.Change.TryGetProperty("ipv4Enabled", out _));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(nic with { IPv4 = ip with { Enabled = false } })));
        Assert.True(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(nic with { IPv4 = ip with { Enabled = false }, IPv6 = ip with { Enabled = false } })));
        Assert.Throws<ArgumentException>(() => new DeviceNetworkProtocols().Validate());
        Assert.Throws<ArgumentException>(() => new DeviceNetworkProtocols(IPv6Enabled: false).Validate(nic with { IPv6 = null }));
    }
}
