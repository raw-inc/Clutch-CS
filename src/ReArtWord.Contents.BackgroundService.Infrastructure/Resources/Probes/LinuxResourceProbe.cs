using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>Linuxの公開procファイルからCPUと物理メモリを観測します。</summary>
internal sealed class LinuxResourceProbe
{
    private double? previousTotal;
    private double previousIdle;

    /// <summary>全CPU累積差分とmeminfoの現在値を取得します。</summary>
    /// <returns>初回差分待ちと取得不能を含む実測値です。</returns>
    internal IReadOnlyList<ResourceMetric> Capture()
    {
        List<ResourceMetric> result = [];
        try
        {
            Dictionary<string, double> memory = new(StringComparer.Ordinal);
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (2 <= parts.Length && double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out double kilobytes))
                {
                    memory[parts[0].TrimEnd(':')] = kilobytes * 1024;
                }
            }
            double totalMemory = memory["MemTotal"];
            double available = memory["MemAvailable"];
            result.Add(new ResourceMetric("memory.used", "machine", totalMemory - available, "bytes"));
            result.Add(new ResourceMetric("memory.available", "machine", available, "bytes"));
            result.Add(new ResourceMetric("memory.total", "machine", totalMemory, "bytes"));
            result.Add(new ResourceMetric("memory.usage", "machine", (totalMemory - available) / totalMemory * 100, "percent"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            result.Add(new ResourceMetric("memory.usage", "machine", null, "percent", "resource.memoryUnavailable"));
        }
        try
        {
            string[] parts = File.ReadLines("/proc/stat").First().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            double[] times = parts.Skip(1).Take(8).Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            double total = times.Sum();
            double idle = times[3] + times[4];
            double? usage = null;
            string? reason = "resource.cpuInitialSample";
            if (previousTotal.HasValue && previousTotal.Value < total)
            {
                double elapsed = total - previousTotal.Value;
                usage = Math.Clamp((elapsed - (idle - previousIdle)) / elapsed * 100, 0, 100);
                reason = null;
            }
            previousTotal = total;
            previousIdle = idle;
            result.Add(new ResourceMetric("cpu.usage", "machine", usage, "percent", reason));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or InvalidOperationException)
        {
            result.Add(new ResourceMetric("cpu.usage", "machine", null, "percent", "resource.cpuUnavailable"));
        }
        return result;
    }
}
