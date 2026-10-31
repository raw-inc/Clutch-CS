using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実PowerShellの型・parameter bindingを使い、OS変更の入口はメモリー内へ置き換えます。</summary>
public sealed class DevicePowerShellTests
{
    /// <summary>Firewallの列挙引数、正確なルール一致、管理対象外の拒否を確認します。</summary>
    /// <returns>実OSを変更しないparameter bindingの検証です。</returns>
    [WindowsTheory]
    [InlineData("firewallProfile", false, true)]
    [InlineData("firewallRuleEnabled", false, true)]
    [InlineData("firewallRuleEnabled", true, true)]
    [InlineData("firewallRuleRemove", false, true)]
    [InlineData("firewallRuleRemove", false, false)]
    public async Task FirewallUsesEnumParametersAndExactLocalRuleAsync(string action, bool enabled, bool editable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string profileAction = "firewallProfile";
        bool profile = string.Equals(action, profileAction, StringComparison.Ordinal);
        string resourceId = profile ? "profile/Private"
                                    : "rule/target[*]";
        JsonElement result = await RunFixtureAsync("FirewallBoundary", new { action, query = new { resourceId }, change = new { enabled }, before = new { editable } }).ConfigureAwait(true);
        Assert.Equal(editable, result.GetProperty("success").GetBoolean());
        if (editable)
        {
            JsonElement call = Assert.Single(result.GetProperty("calls").EnumerateArray());
            string expected = profile ? "Private"
                                      : "target[*]";
            Assert.Equal(expected, call.GetProperty("name").GetString());
            string enabledKey = "enabled";
            if (call.TryGetProperty(enabledKey, out JsonElement value))
            {
                Assert.Equal(enabled.ToString(), value.GetString());
            }
        }
        else
        {
            Assert.False(result.GetProperty("started").GetBoolean());
            Assert.Empty(result.GetProperty("calls").EnumerateArray());
        }
    }

    /// <summary>AccessとUntaggedを別のOS引数へ写し、未対応モードと名前wildcardを区別します。</summary>
    /// <returns>管理OS用の正確なvNICだけに届くことの確認です。</returns>
    [WindowsTheory]
    [InlineData("Access", 120, true)]
    [InlineData("Access", null, true)]
    [InlineData("Untagged", 200, true)]
    [InlineData("Trunk", 100, false)]
    public async Task VlanChangesDistinguishAccessUntaggedAndUnsupportedModesAsync(string mode, int? vlanId, bool accepted)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await RunFixtureAsync("VirtualNicBoundary", new { mode, vlanId }).ConfigureAwait(true);
        Assert.Equal(accepted, result.GetProperty("success").GetBoolean());
        Assert.Equal(accepted, result.GetProperty("started").GetBoolean());
        if (accepted)
        {
            JsonElement call = Assert.Single(result.GetProperty("calls").EnumerateArray());
            Assert.Equal("target[*]", call.GetProperty("id").GetString());
            Assert.Equal(vlanId is null, call.GetProperty("untagged").GetBoolean());
            Assert.Equal(vlanId is not null, call.GetProperty("access").GetBoolean());
            if (vlanId is not null)
            {
                Assert.Equal(vlanId.Value, call.GetProperty("vlanId").GetInt32());
            }
        }
        else
        {
            Assert.Empty(result.GetProperty("calls").EnumerateArray());
        }
    }

    /// <summary>DNSだけの操作で他の設定関数を呼ばず、対象familyを固定します。</summary>
    /// <returns>実スクリプトの呼出順と部分失敗を検証します。</returns>
    [WindowsTheory]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(4, true)]
    public async Task DnsChangeUsesOnlyTheExactFamilyAndPreservesFailureAsync(int family, bool fail)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string dns = family == 4 ? "10.0.0.53"
                                 : "2001:db8::53";
        DeviceNetworkChange change = new(family, AutomaticDns: false, Dns: [dns]);
        JsonElement result = await RunAsync(change, fail).ConfigureAwait(true);
        Assert.Equal(!fail, result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("started").GetBoolean());
        JsonElement call = Assert.Single(result.GetProperty("calls").EnumerateArray());
        Assert.Equal("dns", call.GetProperty("kind").GetString());
        Assert.Equal(42, call.GetProperty("index").GetInt32());
        int expectedFamily = family == 4 ? 2
                                         : 23;
        Assert.Equal(expectedFamily, call.GetProperty("family").GetInt32());
        Assert.Equal(dns, Assert.Single(call.GetProperty("servers").EnumerateArray()).GetString());
        Assert.Equal(fail ? 0
                          : 1, result.GetProperty("steps").GetArrayLength());
    }

    /// <summary>DHCPとmetricを明示した場合だけ、両方を選択した同一IFへ送ります。</summary>
    /// <returns>入力文字列がコードにならない確認です。</returns>
    [WindowsFact]
    public async Task ExplicitAddressingAndMetricAreIndependentAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await RunAsync(new(4, Dhcp: false, AutomaticMetric: false, Metric: 17), false).ConfigureAwait(true);
        Assert.True(result.GetProperty("success").GetBoolean());
        JsonElement[] calls = result.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(42, call.GetProperty("index").GetInt32()));
        Assert.Equal("Disabled", calls[0].GetProperty("dhcp").GetString());
        Assert.Equal(17, calls[1].GetProperty("metric").GetInt32());
    }

    /// <summary>製品の変更処理を固定のテスト境界と共に標準入力へ渡します。</summary>
    /// <returns>OS変更なしで実行したJSONです。</returns>
    private static async Task<JsonElement> RunAsync(DeviceNetworkChange change, bool fail, CancellationToken cancellationToken = default)
    {
        object request = new { id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", name = "日本語 NIC[*]; throw 'must remain data'", change, fail };
        return await RunFixtureAsync("NetworkBoundary", request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>実PowerShellにOSを呼べないfixtureを渡します。</summary>
    /// <returns>製品スクリプトを通した呼出記録です。</returns>
    internal static async Task<JsonElement> RunFixtureAsync(string fixture, object request, CancellationToken cancellationToken = default)
    {
        string firewall = "Firewall";
        string area = "Network.Change";
        if (fixture.StartsWith(firewall, StringComparison.Ordinal))
        {
            area = "Firewall.Change";
        }
        string networkRead = string.Empty;
        string networkFixture = "NetworkReadBoundary";
        if (string.Equals(fixture, networkFixture, StringComparison.Ordinal))
        {
            networkRead = Resource("Network.Read") + "\n";
        }
        string code = Resource("Common") + "\n" + Resource(area) + "\n"
            + Resource("Firewall.Read") + "\n"
            + Resource("VirtualSwitch.Read") + "\n" + Resource("VirtualSwitch.Change") + "\n"
            + networkRead + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "DeviceManagement", fixture + ".ps1"), cancellationToken).ConfigureAwait(false);
        string bootstrap = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $frame=[Console]::In.ReadToEnd() | ConvertFrom-Json; $request=$frame.request; & ([scriptblock]::Create($frame.code))";
        string output = await BoundedProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap))],
            new ProcessRunLimits(StandardInput: JsonSerializer.Serialize(new { code, request }, ContractJson.Options), OutputEncoding: Encoding.UTF8), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(output);
    }

    /// <summary>検証対象そのものの埋込スクリプトを読みます。</summary>
    /// <returns>製品の固定処理です。</returns>
    private static string Resource(string name)
    {
        using Stream stream = typeof(DeviceManagementService).Assembly.GetManifestResourceStream("Contents.DeviceManagement." + name + ".ps1")!;
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
