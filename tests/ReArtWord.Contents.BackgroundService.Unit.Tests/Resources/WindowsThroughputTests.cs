using System.Net.NetworkInformation;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>実WindowsのNIC統計とディスクI/Oでnativeカウンター境界を確認します。</summary>
public sealed class WindowsThroughputTests
{
    /// <summary>実OSの64bit統計と一致する範囲を読み取り、native配置の誤りを検知します。</summary>
    [Fact]
    public void PhysicalNicCountersBracketManagedInterfaceStatistics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        IReadOnlyList<TransferCounter> before = Assert.IsAssignableFrom<IReadOnlyList<TransferCounter>>(WindowsNetworkThroughput.Read());
        Dictionary<string, IPInterfaceStatistics> statistics = new(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            statistics[Guid.Parse(network.Id).ToString("D")] = network.GetIPStatistics();
        }
        IReadOnlyList<TransferCounter> after = Assert.IsAssignableFrom<IReadOnlyList<TransferCounter>>(WindowsNetworkThroughput.Read());
        foreach (TransferCounter start in before)
        {
            TransferCounter? end = after.Where(counter => string.Equals(counter.Identity, start.Identity, StringComparison.Ordinal)).Cast<TransferCounter?>().FirstOrDefault();
            if (end is not { } current || current.Read < start.Read || current.Write < start.Write || !statistics.TryGetValue(start.Identity.Split('/')[0], out IPInterfaceStatistics? managed))
            {
                continue;
            }
            Assert.InRange(checked((ulong)managed.BytesReceived), start.Read, current.Read);
            Assert.InRange(checked((ulong)managed.BytesSent), start.Write, current.Write);
        }
    }

    /// <summary>同期Flushした試験ファイルの書込みが、次のディスク速度観測へ反映されます。</summary>
    /// <returns>実カウンターを用いた取得と破棄の検証です。</returns>
    // 抑制理由: OSキャッシュへの書込みだけを測らないため、非同期版にないflushToDisk=trueを明示します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1849", Justification = "物理ディスクへの書込みを検証するため、Flush(true)の同期完了を必要とします。")]
    [Fact]
    public async Task PhysicalDiskQueryMeasuresRealFileWritesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using MediaTestFiles files = new();
        using WindowsDiskThroughput disk = WindowsDiskThroughput.Create();
        Assert.All(disk.Capture(), metric => Assert.Null(metric.Value));
        await Task.Delay(200).ConfigureAwait(true);
        string path = Path.Combine(files.Root, "throughput.bin");
        using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
        {
            byte[] buffer = new byte[1024 * 1024];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
            for (int index = 0; index < 8; index++)
            {
                file.Write(buffer);
            }
            file.Flush(true);
        }
        IReadOnlyList<ResourceMetric> measured = disk.Capture();
        Assert.True(0 < measured[1].Value, measured[1].Reason);
        Assert.All(measured, metric =>
        {
            Assert.NotNull(metric.Value);
            Assert.True(double.IsFinite(metric.Value.Value));
            Assert.Null(metric.Reason);
        });
    }
}
