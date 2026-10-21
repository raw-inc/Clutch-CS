using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>物理ディスク全体の転送速度を、再利用するOSカウンターで取得します。</summary>
internal sealed class WindowsDiskThroughput : IDisposable
{
    private readonly IPerformanceCounterQuery read;
    private readonly IPerformanceCounterQuery write;

    /// <summary>観測器の寿命で破棄する二つの照会を受け取ります。</summary>
    internal WindowsDiskThroughput(IPerformanceCounterQuery read, IPerformanceCounterQuery write)
    {
        this.read = read;
        this.write = write;
    }

    /// <summary>言語中立のnativeカウンターを所有します。</summary>
    [SupportedOSPlatform("windows")]
    private WindowsDiskThroughput()
    {
        read = new WindowsPerformanceQuery(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        write = new WindowsPerformanceQuery(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
    }

    /// <summary>ローカライズされたカウンター名に依存しない照会を構成します。</summary>
    /// <returns>呼出し元が破棄する観測器です。</returns>
    [SupportedOSPlatform("windows")]
    internal static WindowsDiskThroughput Create() => new();

    /// <summary>OSの合計実体だけを採用し、各ディスクとの重複加算を避けます。</summary>
    /// <returns>ディスク読出し・書込みのbytes/sです。</returns>
    internal IReadOnlyList<ResourceMetric> Capture() => [Metric("disk.read.rate", read.Collect()), Metric("disk.write.rate", write.Collect())];

    /// <summary>照会と所属カウンターを収集サービスの終了時に解放します。</summary>
    public void Dispose()
    {
        read.Dispose();
        write.Dispose();
    }

    /// <summary>初回・取得不能・不正値をゼロに置換しません。</summary>
    /// <returns>有限で非負の合計値、または欠測です。</returns>
    private static ResourceMetric Metric(string name, IReadOnlyList<PerformanceCounterValue>? values)
    {
        string total = "_Total";
        double? value = values?.FirstOrDefault(item => string.Equals(item.Instance, total, StringComparison.Ordinal))?.Value;
        if (value is double number && double.IsFinite(number) && 0 <= number)
        {
            return new(name, "machine", number, "bytes/s");
        }
        return new(name, "machine", null, "bytes/s", "resource.diskRateUnavailableOrInitial");
    }
}
