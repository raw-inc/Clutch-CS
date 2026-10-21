using System.Globalization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>Linuxのsysfsから、二重計上しない機器の累積転送量を取得します。</summary>
internal static class LinuxTransferCounters
{
    /// <summary>パーティション・device-mapper・loopを除き、デバイスを持つ全体ディスクを取得します。</summary>
    /// <returns>512byteセクターをバイト数へ変換したカウンターです。</returns>
    internal static IReadOnlyList<TransferCounter>? Disks(string root = "/sys")
    {
        try
        {
            List<TransferCounter> values = [];
            foreach (string device in Directory.EnumerateDirectories(Path.Combine(root, "block")))
            {
                string backingDevice = Path.Combine(device, "device");
                if (!Directory.Exists(backingDevice))
                {
                    continue;
                }
                string identity = device;
                string sequence = Path.Combine(device, "diskseq");
                if (File.Exists(sequence))
                {
                    identity += "/" + File.ReadAllText(sequence).Trim();
                }
                values.Add(ParseDisk(identity, File.ReadAllText(Path.Combine(device, "stat"))));
            }
            return values;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>物理デバイス階層のNICだけを読み取り、ブリッジやVPNを加算しません。</summary>
    /// <returns>受信・送信の累積バイト数です。</returns>
    internal static IReadOnlyList<TransferCounter>? Network(string root = "/sys")
    {
        try
        {
            List<TransferCounter> values = [];
            foreach (string device in Directory.EnumerateDirectories(Path.Combine(root, "class", "net")))
            {
                if (!UnixWakeHardware.Linux(Path.GetFileName(device), root))
                {
                    continue;
                }
                string identity = device + "/" + File.ReadAllText(Path.Combine(device, "ifindex")).Trim();
                ulong received = ulong.Parse(File.ReadAllText(Path.Combine(device, "statistics", "rx_bytes")).Trim(), CultureInfo.InvariantCulture);
                ulong sent = ulong.Parse(File.ReadAllText(Path.Combine(device, "statistics", "tx_bytes")).Trim(), CultureInfo.InvariantCulture);
                values.Add(new(identity, received, sent));
            }
            return values;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>sysfsの固定512byteセクターを、論理セクターサイズによらず変換します。</summary>
    /// <returns>全体ディスク一台分の累積値です。</returns>
    internal static TransferCounter ParseDisk(string identity, string text)
    {
        string[] fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 11)
        {
            throw new FormatException("Disk statistics are incomplete.");
        }
        return new(identity, checked(ulong.Parse(fields[2], CultureInfo.InvariantCulture) * 512),
            checked(ulong.Parse(fields[6], CultureInfo.InvariantCulture) * 512));
    }
}
