using System.Net.NetworkInformation;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>OS実測値とdrive・NICごとの取得状態を集約します。</summary>
internal sealed class ResourceHardwareProbe
{
    private readonly WindowsResourceProbe? windows;
    private readonly LinuxResourceProbe linux = new();

    /// <summary>実行OSにだけ対応する観測器を作ります。</summary>
    internal ResourceHardwareProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            windows = new WindowsResourceProbe();
        }
    }

    /// <summary>現在取得できる値と取得不能理由を同じ一覧へ返します。</summary>
    /// <returns>対象を識別できる実metricです。</returns>
    internal IReadOnlyList<ResourceMetric> Capture()
    {
        List<ResourceMetric> metrics = [];
        if (OperatingSystem.IsWindows())
        {
            metrics.AddRange(windows!.Capture());
        }
        else
        {
            if (OperatingSystem.IsLinux())
            {
                metrics.AddRange(linux.Capture());
            }
            else
            {
                if (!OperatingSystem.IsMacOS())
                {
                    metrics.Add(new ResourceMetric("cpu.usage", "machine", null, "percent", "resource.osUnsupported"));
                    metrics.Add(new ResourceMetric("memory.usage", "machine", null, "percent", "resource.osUnsupported"));
                }
            }
        }
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                {
                    continue;
                }
                if (drive.IsReady)
                {
                    metrics.Add(new ResourceMetric("disk.available", drive.Name, drive.AvailableFreeSpace, "bytes"));
                    metrics.Add(new ResourceMetric("disk.total", drive.Name, drive.TotalSize, "bytes"));
                }
                else
                {
                    metrics.Add(new ResourceMetric("disk.available", drive.Name, null, "bytes", "resource.driveNotReady"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                metrics.Add(new ResourceMetric("disk.available", drive.Name, null, "bytes", "resource.driveUnavailable"));
            }
        }
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                IPInterfaceStatistics statistics = network.GetIPStatistics();
                metrics.Add(new ResourceMetric("network.received", network.Id, statistics.BytesReceived, "bytes"));
                metrics.Add(new ResourceMetric("network.sent", network.Id, statistics.BytesSent, "bytes"));
            }
            catch (NetworkInformationException)
            {
                metrics.Add(new ResourceMetric("network.received", network.Id, null, "bytes", "resource.networkUnavailable"));
            }
        }
        return metrics;
    }
}
