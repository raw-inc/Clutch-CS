using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;

/// <summary>Linux・macOSのMACとIPv4を共通APIから取り、WoL用の有限一覧へ変換します。</summary>
internal static class UnixWakeInventory
{
    /// <summary>リンクが切れているNICも含め、停止前の送信対象を列挙します。</summary>
    /// <returns>未確認NICを手動選択できる、OS共通形式の一覧です。</returns>
    internal static async Task<DeviceWakeInventory> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsMacOS())
        {
            IReadOnlySet<string> physical = await UnixWakeHardware.MacAsync(cancellationToken).ConfigureAwait(false);
            return Capture(NetworkInterface.GetAllNetworkInterfaces(), adapter => physical.Contains(adapter.Name), "macOS");
        }
        return Capture(NetworkInterface.GetAllNetworkInterfaces(), adapter => UnixWakeHardware.Linux(adapter.Name), "Linux");
    }

    /// <summary>列挙順に依存する版変更を避け、任意本文を送れるようなMACは除きます。</summary>
    /// <returns>同じOS情報からは同じ識別子と内容版になる一覧です。</returns>
    internal static DeviceWakeInventory Capture(NetworkInterface[] adapters, Func<NetworkInterface, bool> physical, string platform)
    {
        if (128 < adapters.Length)
        {
            throw new InvalidOperationException("Device.ReadIncomplete");
        }
        List<DeviceWakeInterface> interfaces = [];
        HashSet<DeviceIpAddress> addresses = [];
        foreach (NetworkInterface adapter in adapters.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            foreach (UnicastIPAddressInformation item in adapter.GetIPProperties().UnicastAddresses)
            {
                if (item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address))
                {
                    addresses.Add(new(item.Address.ToString(), item.PrefixLength));
                }
            }
            string mac = adapter.GetPhysicalAddress().ToString();
            try
            {
                _ = WakeOnLanSender.Packet(mac);
            }
            catch (ArgumentException)
            {
                continue;
            }
            string id = Identity(platform, adapter.Name, mac);
            interfaces.Add(new(id, adapter.Name, mac, physical(adapter)));
        }
        DeviceWakeInventory inventory = new(interfaces, addresses.OrderBy(item => item.Address, StringComparer.Ordinal).ThenBy(item => item.PrefixLength).ToArray());
        new DeviceWakeState(new(true, []), inventory).Validate();
        return inventory;
    }

    /// <summary>既存GUID契約を維持し、同名NICの交換やMAC変更では旧選択を流用しません。</summary>
    /// <returns>OS・NIC名・確認したMACに固定した識別子です。</returns>
    internal static string Identity(string platform, string name, string mac)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("contents.wake.nic.v1\0" + platform + "\0" + name + "\0" + mac)).AsSpan(0, 16)).ToString("D");
}
