using System.ComponentModel;
using System.Text;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;

/// <summary>追加ソフトを導入せず、OSの情報から物理NICをbest-effortで判定します。</summary>
internal static class UnixWakeHardware
{
    /// <summary>sysfsの実デバイス階層を確認し、仮想NICや確認不能を既定対象から除きます。</summary>
    /// <returns>物理側のデバイス階層にあると確認できた場合だけtrueです。</returns>
    internal static bool Linux(string name, string root = "/sys")
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            FileSystemInfo? target = new DirectoryInfo(Path.Combine(root, "class", "net", name)).ResolveLinkTarget(true);
            if (target is null || !target.Exists)
            {
                return false;
            }
            string devices = Path.GetFullPath(Path.Combine(root, "devices")) + Path.DirectorySeparatorChar;
            string virtualDevices = Path.Combine(devices, "virtual") + Path.DirectorySeparatorChar;
            return target.FullName.StartsWith(devices, StringComparison.Ordinal) && !target.FullName.StartsWith(virtualDevices, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>macOS標準コマンドを有限時間で読み取り、利用不能なら未確認の一覧を返します。</summary>
    /// <returns>ハードウェアポートとして確認したEthernet系のBSD名です。</returns>
    internal static async Task<IReadOnlySet<string>> MacAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            string output = await BoundedProcessRunner.RunAsync("/usr/sbin/networksetup", ["-listallhardwareports"],
                new ProcessRunLimits(65536, true, OutputEncoding: Encoding.UTF8, EnvironmentOverrides: new Dictionary<string, string?> { ["LC_ALL"] = "C", ["LANG"] = "C" }), timeout.Token).ConfigureAwait(false);
            return ParseMac(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>ブリッジ・VLAN・トンネルをハードウェアポートと誤認しません。</summary>
    /// <returns>物理ポート一覧に載ったen番号の集合です。</returns>
    internal static IReadOnlySet<string> ParseMac(string output)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        bool hardware = false;
        string portPrefix = "Hardware Port:", devicePrefix = "Device:", ethernetPrefix = "en";
        foreach (string line in output.Split('\n'))
        {
            string text = line.Trim();
            if (text.Length == 0)
            {
                hardware = false;
                continue;
            }
            if (text.StartsWith(portPrefix, StringComparison.Ordinal))
            {
                hardware = !string.IsNullOrWhiteSpace(text[portPrefix.Length..]);
                continue;
            }
            if (hardware && text.StartsWith(devicePrefix, StringComparison.Ordinal))
            {
                string name = text[devicePrefix.Length..].Trim();
                if (name.StartsWith(ethernetPrefix, StringComparison.Ordinal) && 2 < name.Length && name.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
                {
                    result.Add(name);
                }
                hardware = false;
            }
        }
        return result;
    }
}
