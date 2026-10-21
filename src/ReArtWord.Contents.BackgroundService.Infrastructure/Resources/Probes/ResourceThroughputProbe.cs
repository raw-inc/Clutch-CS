using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>ホスト全体のディスクと物理NICの速度を、有限の前回値から観測します。</summary>
internal sealed class ResourceThroughputProbe : IDisposable
{
    private readonly WindowsDiskThroughput? windowsDisk;
    private readonly CumulativeThroughput disk = new("disk.read.rate", "disk.write.rate");
    private readonly CumulativeThroughput network = new("network.received.rate", "network.sent.rate");

    /// <summary>Windowsカウンターは収集サービスの寿命で再利用します。</summary>
    internal ResourceThroughputProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            windowsDisk = WindowsDiskThroughput.Create();
        }
    }

    /// <summary>対象OSの観測のみを行い、未対応や欠測をゼロへ変えません。</summary>
    /// <returns>bytes/s単位の4つのホスト合計です。</returns>
    internal IReadOnlyList<ResourceMetric> Capture()
    {
        if (OperatingSystem.IsWindows())
        {
            IReadOnlyList<TransferCounter>? interfaces = WindowsNetworkThroughput.Read();
            TimeSpan now = Stopwatch.GetElapsedTime(0);
            return [.. windowsDisk!.Capture(), .. network.Capture(interfaces, now)];
        }
        if (OperatingSystem.IsLinux())
        {
            IReadOnlyList<TransferCounter>? disks = LinuxTransferCounters.Disks();
            IReadOnlyList<TransferCounter>? interfaces = LinuxTransferCounters.Network();
            TimeSpan now = Stopwatch.GetElapsedTime(0);
            return [.. disk.Capture(disks, now), .. network.Capture(interfaces, now)];
        }
        return [new("disk.read.rate", "machine", null, "bytes/s", "resource.osUnsupported"),
            new("disk.write.rate", "machine", null, "bytes/s", "resource.osUnsupported"),
            new("network.received.rate", "machine", null, "bytes/s", "resource.osUnsupported"),
            new("network.sent.rate", "machine", null, "bytes/s", "resource.osUnsupported")];
    }

    /// <summary>保持しているnative照会を解放します。</summary>
    public void Dispose() => windowsDisk?.Dispose();
}
