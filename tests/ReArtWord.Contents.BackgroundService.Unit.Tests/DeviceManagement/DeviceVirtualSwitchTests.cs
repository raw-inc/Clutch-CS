using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>仮想スイッチの固定対象、OS境界、反映照合を実設定と分離して確認します。</summary>
public sealed class DeviceVirtualSwitchTests
{
    /// <summary>種別ごとの引数と実状態を照合し、同名だけで成功にしません。</summary>
    /// <returns>実PowerShellを使った変更と型付き再読取りの確認です。</returns>
    [Theory]
    [InlineData("Internal", null)]
    [InlineData("Private", null)]
    [InlineData("External", true)]
    [InlineData("External", false)]
    public async Task CreatesAndVerifiesEachKindAsync(string kind, bool? shared)
    {
        DeviceVirtualSwitchChange change = new("日本語 ; harmless", kind);
        if (shared is not null)
        {
            change = change with { PhysicalInterfaceId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", AllowManagementOS = shared };
        }
        DeviceChangeRequest request = Request(EDeviceAction.VirtualSwitchCreate, "switches", change);
        DeviceChangeRules.Validate(request);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("VirtualSwitchBoundary", new { action = "virtualSwitchCreate", scenario = "normal", change });
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("started").GetBoolean());
        JsonElement call = Assert.Single(result.GetProperty("calls").EnumerateArray());
        Assert.Equal(change.Name, call.GetProperty("name").GetString());
        DeviceSnapshot after = DeviceSnapshots.Create(result.GetProperty("after"));
        Assert.True(DeviceChangeConfirmation.Matches(request, after));
        DeviceVirtualSwitchState state = DeviceSnapshots.Read<DeviceVirtualSwitchState>(after.Data);
        DeviceVirtualSwitchDetails created = state.Switches.Single(item => string.Equals(item.Switch.Name, change.Name, StringComparison.Ordinal));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(state with { Switches = [created with { AllowManagementOS = !created.AllowManagementOS }] })));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(state with { Switches = [created, created] })));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(state with { Switches = [created with { PhysicalInterfaceIds = [Guid.NewGuid().ToString()] }] })));
    }

    /// <summary>名前をwildcard検索せず、管理OSの接続だけのスイッチを識別子で削除します。</summary>
    /// <returns>指定したスイッチの消失まで確認します。</returns>
    [Fact]
    public async Task DeletesExactSwitchAndChecksAbsenceAsync()
    {
        DeviceChangeRequest request = Request(EDeviceAction.VirtualSwitchDelete, "switch/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new());
        DeviceChangeRules.Validate(request);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("VirtualSwitchBoundary", new { action = "virtualSwitchDelete", scenario = "normal", change = new DeviceVirtualSwitchChange() });
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Assert.Single(result.GetProperty("calls").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(result.GetProperty("after"))));
        Assert.False(DeviceChangeConfirmation.Matches(request, DeviceSnapshots.Create(result.GetProperty("before"))));
    }

    /// <summary>変更前に判明した危険・競合と、開始後のOS障害を区別します。</summary>
    /// <returns>再実行や別対象への変更を行わない確認です。</returns>
    [Theory]
    [InlineData("virtualSwitchDelete", "guest", "Device.VirtualSwitchInUse", false)]
    [InlineData("virtualSwitchDelete", "protected", "Device.VirtualSwitchReadOnly", false)]
    [InlineData("virtualSwitchDelete", "changed", "Device.SettingsChanged", false)]
    [InlineData("virtualSwitchCreate", "changed", "Device.SettingsChanged", false)]
    [InlineData("virtualSwitchCreate", "occupied", "Device.PhysicalInterfaceInUse", false)]
    [InlineData("virtualSwitchCreate", "absent", "Device.PhysicalInterfaceInUse", false)]
    [InlineData("virtualSwitchCreate", "ambiguous", "Device.PhysicalInterfaceInUse", false)]
    [InlineData("virtualSwitchCreate", "duplicate", "Device.VirtualSwitchExists", false)]
    [InlineData("virtualSwitchCreate", "failure", "Device.NativeCommandFailed", true)]
    [InlineData("virtualSwitchDelete", "failure", "Device.NativeCommandFailed", true)]
    public async Task PreservesFailureBoundaryAsync(string action, string scenario, string reason, bool started)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DeviceVirtualSwitchChange change = new("created", "External", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", true);
        JsonElement result = await DevicePowerShellTests.RunFixtureAsync("VirtualSwitchBoundary", new { action, scenario, change });
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(started, result.GetProperty("started").GetBoolean());
        Assert.Equal(reason, result.GetProperty("reason").GetString());
        if (started)
        {
            Assert.Single(result.GetProperty("calls").EnumerateArray());
        }
        else
        {
            Assert.Empty(result.GetProperty("calls").EnumerateArray());
        }
        Assert.Empty(result.GetProperty("steps").EnumerateArray());
    }

    /// <summary>別領域・自由な削除対象・不要な作成引数・無確認の通信変更を受け付けません。</summary>
    [Fact]
    public void RejectsInvalidTargetsAndArguments()
    {
        DeviceChangeRequest request = Request(EDeviceAction.VirtualSwitchCreate, "switches", new("private", "Private"));
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.Validate(request with { AcknowledgeDisconnect = false }));
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.Validate(request with { Read = new(EDeviceArea.Hosts) }));
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.Validate(request with { Read = new(EDeviceArea.Network) }));
        foreach (DeviceVirtualSwitchChange change in new DeviceVirtualSwitchChange[] { new("wild*", "Private"), new(" padded ", "Private"), new("", "Private"), new("name", "External"), new("name", "Internal", AllowManagementOS: true), new("name", "Private", Guid.NewGuid().ToString()), new("name", "Other") })
        {
            Assert.ThrowsAny<ArgumentException>(() => DeviceChangeRules.Validate(request with { Change = DeviceSnapshots.Create(change).Data }));
        }
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.Validate(Request(EDeviceAction.VirtualSwitchDelete, "switch/*", new())));
        Assert.Throws<ArgumentException>(() => DeviceChangeRules.Validate(Request(EDeviceAction.VirtualSwitchDelete, "switch/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new(Name: "unexpected"))));
    }

    /// <summary>入力検証へ渡す固定対象を構築します。</summary>
    /// <returns>通信影響の確認を含む要求です。</returns>
    private static DeviceChangeRequest Request(EDeviceAction action, string resource, DeviceVirtualSwitchChange change) => new(Guid.NewGuid(), "host", Guid.NewGuid(), action,
        new(EDeviceArea.Network, resource), new string('a', 64), DeviceSnapshots.Create(change).Data, true);
}
