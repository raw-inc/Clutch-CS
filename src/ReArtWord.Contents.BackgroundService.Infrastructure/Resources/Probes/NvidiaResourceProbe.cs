using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>NVIDIA公式CLIの読み取り専用照会でGPU実測値を取得します。</summary>
internal static class NvidiaResourceProbe
{
    /// <summary>明示された実行物だけを使い、N/Aを取得不能のまま保持します。</summary>
    /// <returns>GPU UUIDごとの取得状態付きmetricです。</returns>
    internal static async Task<IReadOnlyList<ResourceMetric>> CaptureAsync(string? executable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            return [new ResourceMetric("gpu.usage", "machine", null, "percent", "resource.nvidiaAdapterUnavailable")];
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            string[] arguments = ["--query-gpu=uuid,utilization.gpu,memory.used,memory.total,temperature.gpu", "--format=csv,noheader,nounits"];
            string output = await BoundedProcessRunner.RunAsync(executable, arguments, linked.Token).ConfigureAwait(false);
            List<ResourceMetric> metrics = [];
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length != 5)
                {
                    throw new InvalidDataException("GPU観測の公開形式が一致しません。");
                }
                string target = fields[0];
                metrics.Add(Metric("gpu.usage", target, fields[1], "percent", 1));
                metrics.Add(Metric("gpu.memory.used", target, fields[2], "bytes", 1048576));
                metrics.Add(Metric("gpu.memory.total", target, fields[3], "bytes", 1048576));
                metrics.Add(Metric("gpu.temperature", target, fields[4], "celsius", 1));
            }
            if (metrics.Count == 0)
            {
                return [new ResourceMetric("gpu.usage", "machine", null, "percent", "resource.gpuNotDetected")];
            }
            return metrics;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception or ProcessStopUnconfirmedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [new ResourceMetric("gpu.usage", "machine", null, "percent", "resource.gpuUnavailable")];
        }
    }

    /// <summary>N/Aや未対応値をゼロに変えずmetricへ写します。</summary>
    /// <returns>単位を揃えた値または取得不能です。</returns>
    private static ResourceMetric Metric(string name, string target, string text, string unit, double multiplier)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
        {
            return new ResourceMetric(name, target, value * multiplier, unit);
        }
        return new ResourceMetric(name, target, null, unit, "resource.gpuMetricUnavailable");
    }
}
