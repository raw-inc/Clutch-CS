using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>macOS標準の読み取り専用コマンドを有限時間で観測します。</summary>
internal static class MacResourceProbe
{
    /// <summary>独立したCPUと物理メモリ観測を行い、一方の失敗を他方へ広げません。</summary>
    /// <returns>実値と取得不能理由です。</returns>
    internal static async Task<IReadOnlyList<ResourceMetric>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        List<ResourceMetric> metrics = [];
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string output = await BoundedProcessRunner.RunAsync("/usr/bin/top", ["-l", "2", "-s", "1", "-n", "0"], timeout.Token).ConfigureAwait(false);
            metrics.Add(MacResourceParser.Cpu(output));
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or ProcessStopUnconfirmedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metrics.Add(new("cpu.usage", "machine", null, "percent", "resource.cpuUnavailable"));
        }
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string total = await BoundedProcessRunner.RunAsync("/usr/sbin/sysctl", ["-n", "hw.memsize"], timeout.Token).ConfigureAwait(false);
            string pages = await BoundedProcessRunner.RunAsync("/usr/bin/vm_stat", [], timeout.Token).ConfigureAwait(false);
            metrics.AddRange(MacResourceParser.Memory(pages, total));
        }
        catch (Exception exception) when (exception is IOException or OverflowException or OperationCanceledException or System.ComponentModel.Win32Exception or ProcessStopUnconfirmedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metrics.Add(new("memory.usage", "machine", null, "percent", "resource.memoryUnavailable"));
        }
        return metrics;
    }
}
