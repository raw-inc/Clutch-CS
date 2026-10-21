using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>CPUの識別情報を使用率と分けて取得します。</summary>
internal static class CpuInformation
{
    /// <summary>OSから取得不能な仕様は推測値で埋めません。</summary>
    /// <returns>コア数・スレッド数・CPU名のmetricです。</returns>
    internal static async Task<IReadOnlyList<ResourceMetric>> ReadAsync(CancellationToken cancellationToken = default)
    {
        (double? Cores, double? Threads, string? Name) information = (null, null, null);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                information = WindowsCpuInformation.Read();
            }
            if (OperatingSystem.IsLinux())
            {
                information = ReadLinux();
            }
            if (OperatingSystem.IsMacOS())
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                string cores = await BoundedProcessRunner.RunAsync("/usr/sbin/sysctl", ["-n", "hw.physicalcpu"], timeout.Token).ConfigureAwait(false);
                string threads = await BoundedProcessRunner.RunAsync("/usr/sbin/sysctl", ["-n", "hw.logicalcpu"], timeout.Token).ConfigureAwait(false);
                information = (Count(cores), Count(threads), null);
                string name = await BoundedProcessRunner.RunAsync("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"], timeout.Token).ConfigureAwait(false);
                information.Name = name.Trim();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception or OperationCanceledException or ProcessStopUnconfirmedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return [new("cpu.cores", "machine", information.Cores, "count", Missing(information.Cores)),
            new("cpu.threads", "machine", information.Threads, "count", Missing(information.Threads)),
            new("cpu.name", "machine", null, "text", Missing(information.Name), information.Name)];
    }

    /// <summary>オンラインCPUのtopology IDを数え、SMTを物理コアと混同しません。</summary>
    /// <returns>Linuxの実CPU情報です。</returns>
    private static (double? Cores, double? Threads, string? Name) ReadLinux()
    {
        string processorKey = "processor";
        string modelKey = "model name";
        List<string> names = [];
        HashSet<string> cores = new(StringComparer.Ordinal);
        int threads = 0;
        bool complete = true;
        string coreKey = "core_id";
        string packageKey = "physical_package_id";
        string unknown = "-1";
        foreach (string line in File.ReadLines("/proc/cpuinfo"))
        {
            string[] parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }
            if (string.Equals(parts[0], modelKey, StringComparison.Ordinal) && parts[1].Length != 0)
            {
                names.Add(parts[1]);
            }
            if (string.Equals(parts[0], processorKey, StringComparison.Ordinal) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processor))
            {
                threads++;
                string root = "/sys/devices/system/cpu/cpu" + processor.ToString(CultureInfo.InvariantCulture) + "/topology";
                if (File.Exists(Path.Combine(root, coreKey)) && File.Exists(Path.Combine(root, packageKey)))
                {
                    string core = File.ReadAllText(Path.Combine(root, "core_id")).Trim();
                    string package = File.ReadAllText(Path.Combine(root, "physical_package_id")).Trim();
                    complete &= !string.Equals(core, unknown, StringComparison.Ordinal) && !string.Equals(package, unknown, StringComparison.Ordinal);
                    cores.Add(package + ":" + core);
                }
                else
                {
                    complete = false;
                }
            }
        }
        double? physical = null;
        double? logical = null;
        string? label = null;
        if (complete && 0 < cores.Count)
        {
            physical = cores.Count;
        }
        if (0 < threads)
        {
            logical = threads;
        }
        if (0 < names.Count)
        {
            label = string.Join(" / ", names.Distinct(StringComparer.Ordinal));
        }
        return (physical, logical, label);
    }

    /// <summary>OSコマンドの正のCPU数だけを採用します。</summary>
    /// <returns>有効な数またはnullです。</returns>
    private static double? Count(string value)
    {
        if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) && 0 < count)
        {
            return count;
        }
        return null;
    }

    /// <summary>取得不能な識別情報だけに理由を付けます。</summary>
    /// <returns>欠測理由またはnullです。</returns>
    private static string? Missing(object? value)
    {
        if (value is null)
        {
            return "resource.cpuInformationUnavailable";
        }
        return null;
    }
}
