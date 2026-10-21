using System.Net.NetworkInformation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>本番NICやOS設定を変更せず、Unixの一覧・既定判定・PC共通保存を検証します。</summary>
public sealed class UnixWakeTests
{
    /// <summary>列挙順が変わっても、版と明示選択は変わりません。</summary>
    [Fact]
    public void NativeInventoryUsesStableIdsAndAllowsUnconfirmedAdapters()
    {
        NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
        DeviceWakeInventory first = UnixWakeInventory.Capture(adapters, _ => false, "Linux");
        DeviceWakeInventory reordered = UnixWakeInventory.Capture(adapters.Reverse().ToArray(), _ => false, "Linux");
        Assert.Equal(ContractJson.Hash(first), ContractJson.Hash(reordered));
        Assert.NotEmpty(first.Interfaces);
        DeviceWakeState automatic = new(new(true, []), first);
        automatic.Validate();
        Assert.Empty(automatic.Selected());
        string id = first.Interfaces[0].Id;
        DeviceWakeState chosen = automatic with { Selection = new(false, [id]) };
        chosen.Validate();
        Assert.Equal(id, Assert.Single(chosen.Selected()).Id);
        Assert.All(first.Interfaces, item => Assert.True(Guid.TryParse(item.Id, out _)));
    }

    /// <summary>同じ名前の交換NICやMAC変更へ、以前の明示選択を自動で移しません。</summary>
    [Fact]
    public void AdapterIdentityDoesNotFollowReplacements()
    {
        string original = UnixWakeInventory.Identity("Linux", "enp1s0", "021122334455");
        Assert.Equal(original, UnixWakeInventory.Identity("Linux", "enp1s0", "021122334455"));
        Assert.NotEqual(original, UnixWakeInventory.Identity("Linux", "enp1s0", "02AABBCCDDEE"));
        Assert.NotEqual(original, UnixWakeInventory.Identity("Linux", "enp2s0", "021122334455"));
        Assert.NotEqual(original, UnixWakeInventory.Identity("macOS", "enp1s0", "021122334455"));
    }

    /// <summary>Wi-Fi・USB Ethernetを含め、仮想ポートと不完全な行を除外します。</summary>
    [Fact]
    public void MacHardwarePortsExcludeBridgeAndTunnelInterfaces()
    {
        string output = """
            Device: en99
            Hardware Port: Wi-Fi
            Device: en0
            Ethernet Address: 02:11:22:33:44:55

            Hardware Port: USB 10/100/1000 LAN
            Device: en12
            Ethernet Address: 02:11:22:33:44:66

            Hardware Port: Thunderbolt Bridge
            Device: bridge0

            Hardware Port: Tunnel
            Device: utun3

            Hardware Port: Invalid
            Device: en0.10

            Hardware Port:
            Device: en77
            """;
        Assert.Equal(["en0", "en12"], UnixWakeHardware.ParseMac(output).Order(StringComparer.Ordinal));
        Assert.Empty(UnixWakeHardware.ParseMac("Unable to read hardware ports"));
    }

    /// <summary>sysfsのリンク先を辿り、PCI・USBと仮想・欠落したNICを区別します。</summary>
    /// <returns>試験所有の実リンクを通した判定です。</returns>
    [Fact]
    public async Task LinuxReadsTheResolvedDeviceHierarchy()
    {
        string root = Directory.CreateTempSubdirectory("contents-wake-sysfs-").FullName;
        try
        {
            string list = Directory.CreateDirectory(Path.Combine(root, "class", "net")).FullName;
            string physical = Directory.CreateDirectory(Path.Combine(root, "devices", "pci0000", "usb1", "net", "eth0")).FullName;
            string virtualDevice = Directory.CreateDirectory(Path.Combine(root, "devices", "virtual", "net", "br0")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(root, "devices-other", "net", "eth1")).FullName;
            await FileTestDirectory.CreateDirectoryLinkAsync(Path.Combine(list, "eth0"), physical);
            await FileTestDirectory.CreateDirectoryLinkAsync(Path.Combine(list, "br0"), virtualDevice);
            await FileTestDirectory.CreateDirectoryLinkAsync(Path.Combine(list, "eth1"), outside);
            Assert.True(UnixWakeHardware.Linux("eth0", root));
            Assert.False(UnixWakeHardware.Linux("br0", root));
            Assert.False(UnixWakeHardware.Linux("eth1", root));
            Assert.False(UnixWakeHardware.Linux("missing", root));
            Assert.False(UnixWakeHardware.Linux("../eth0", root));
        }
        finally
        {
            // リンク先より先に試験所有のリンクを外し、Windowsのjunctionも確実に片付けます。
            foreach (string name in new[] { "eth0", "br0", "eth1" })
            {
                string link = Path.Combine(root, "class", "net", name);
                if (new DirectoryInfo(link).LinkTarget is not null)
                {
                    Directory.Delete(link);
                }
            }
            Directory.Delete(root, true);
        }
    }

    /// <summary>未保存と明示空を区別し、別Core・再起動後へ変更を共有します。</summary>
    [Fact]
    public void SelectionFileIsSharedAndReadDoesNotWrite()
    {
        string root = Directory.CreateTempSubdirectory("contents-wake-selection-").FullName;
        try
        {
            string path = Path.Combine(root, "host-manager", "wake-selection.json");
            WakeSelectionFile first = new(path), second = new(path);
            Assert.True(first.Read().UsePhysicalDefaults);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            first.Write(new(false, []));
            Assert.False(second.Read().UsePhysicalDefaults);
            Assert.Empty(second.Read().InterfaceIds);
            string nic = UnixWakeInventory.Identity("macOS", "en0", "021122334455");
            second.Write(new(false, [nic]));
            Assert.Equal([nic], first.Read().InterfaceIds);
            DateTime marker = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, marker);
            Assert.Equal([nic], new WakeSelectionFile(path).Read().InterfaceIds);
            Assert.Equal(marker, File.GetLastWriteTimeUtc(path));
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
            Assert.Throws<ArgumentException>(() => first.Write(new(true, [nic])));
            Assert.Equal([nic], second.Read().InterfaceIds);
            File.WriteAllText(path, "invalid");
            Assert.Throws<JsonException>(() => first.Read());
            File.WriteAllText(path, new string(' ', 16385));
            Assert.Throws<InvalidDataException>(() => first.Read());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
