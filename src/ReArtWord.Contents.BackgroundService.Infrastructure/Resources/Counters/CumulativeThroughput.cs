using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>機器ごとの差分を合算し、構成変更やリセットを転送量と誤認しません。</summary>
internal sealed class CumulativeThroughput(string readMetric, string writeMetric)
{
    private readonly Dictionary<string, TransferCounter> previous = new(StringComparer.Ordinal);
    private TimeSpan? previousTime;

    /// <summary>単調時計で速度を算出し、次回に必要な一回分だけを保持します。</summary>
    /// <returns>全対象が比較できた場合のbytes/s、または欠測理由です。</returns>
    internal IReadOnlyList<ResourceMetric> Capture(IReadOnlyList<TransferCounter>? counters, TimeSpan now)
    {
        Dictionary<string, TransferCounter> current = new(StringComparer.Ordinal);
        bool valid = counters is { Count: > 0 };
        foreach (TransferCounter counter in counters ?? [])
        {
            valid &= current.TryAdd(counter.Identity, counter);
        }
        double seconds = (now - previousTime.GetValueOrDefault()).TotalSeconds;
        bool comparable = valid && previousTime.HasValue && 0 < seconds && seconds <= 30 && current.Count == previous.Count;
        double read = 0, write = 0;
        foreach (TransferCounter counter in current.Values)
        {
            if (!previous.TryGetValue(counter.Identity, out TransferCounter before) || counter.Read < before.Read || counter.Write < before.Write)
            {
                comparable = false;
                continue;
            }
            read += counter.Read - before.Read;
            write += counter.Write - before.Write;
        }
        string? reason = null;
        double? readRate = null, writeRate = null;
        if (!comparable)
        {
            reason = "resource.rateInitialOrChanged";
        }
        else
        {
            readRate = read / seconds;
            writeRate = write / seconds;
        }
        previous.Clear();
        previousTime = null;
        if (valid)
        {
            foreach (TransferCounter counter in current.Values)
            {
                previous.Add(counter.Identity, counter);
            }
            previousTime = now;
        }
        else
        {
            reason = "resource.rateUnavailable";
        }
        return [new(readMetric, "machine", readRate, "bytes/s", reason), new(writeMetric, "machine", writeRate, "bytes/s", reason)];
    }
}
