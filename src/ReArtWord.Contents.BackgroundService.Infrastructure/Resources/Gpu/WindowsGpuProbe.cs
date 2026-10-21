using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;

/// <summary>OS 照会を保持し、最初の差分待ちと接続実装ごとの取得不能を明示します。</summary>
internal sealed class WindowsGpuProbe : IDisposable
{
    private readonly IPerformanceCounterQuery query;
    private readonly Func<IReadOnlyList<GpuAdapterIdentity>> adapters;
    private readonly IPerformanceCounterQuery? dedicated;
    private readonly IPerformanceCounterQuery? shared;
    private bool primed;

    /// <summary>実照会と接続実装列挙の所有境界を接続します。</summary>
    internal WindowsGpuProbe(IPerformanceCounterQuery query, Func<IReadOnlyList<GpuAdapterIdentity>> adapters, IPerformanceCounterQuery? dedicated = null, IPerformanceCounterQuery? shared = null)
    {
        this.query = query;
        this.adapters = adapters;
        this.dedicated = dedicated;
        this.shared = shared;
    }

    /// <summary>Windowsのnative 照会を観測器の寿命で所有します。</summary>
    [SupportedOSPlatform("windows")]
    private WindowsGpuProbe()
    {
        query = new WindowsPerformanceQuery(@"\GPU Engine(*)\Utilization Percentage");
        dedicated = new WindowsPerformanceQuery(@"\GPU Adapter Memory(*)\Dedicated Usage");
        shared = new WindowsPerformanceQuery(@"\GPU Adapter Memory(*)\Shared Usage");
        adapters = WindowsGpuAdapters.Read;
    }

    /// <summary>外部プロセスを起動しないWindows標準カウンターを構成します。</summary>
    /// <returns>このサービスが所有するGPU観測器です。</returns>
    [SupportedOSPlatform("windows")]
    internal static WindowsGpuProbe Create() => new();

    /// <summary>明示NVIDIA経路が利用できるときはそのvendorのusageを二重取得結果にしません。</summary>
    /// <returns>初回とinvalid サンプルをゼロにしない測定値です。</returns>
    internal IReadOnlyList<ResourceMetric> Capture(bool nvidiaOwnsUsage)
    {
        GpuAdapterIdentity[] current = adapters().Where(adapter => !nvidiaOwnsUsage || adapter.VendorId != 0x10DE).ToArray();
        IReadOnlyList<PerformanceCounterValue>? counters = query.Collect();
        IReadOnlyList<ResourceMetric> memory = GpuMemoryAggregation.Aggregate(current, dedicated?.Collect(), shared?.Collect());
        bool ready = primed;
        primed = counters is not null;
        if (current.Length == 0)
        {
            if (nvidiaOwnsUsage)
            {
                return [];
            }
            return [new("gpu.usage", "machine", null, "percent", "resource.gpuAdapterUnavailable")];
        }
        if (!ready || counters is null)
        {
            string reason = "resource.gpuInitialSample";
            if (counters is null)
            {
                reason = "resource.gpuCounterUnavailable";
            }
            return [.. current.Select(adapter => new ResourceMetric("gpu.usage", adapter.Luid + "_phys_0", null, "percent", reason, adapter.Name)), .. memory];
        }
        return [.. GpuEngineAggregation.Aggregate(counters, current), .. memory];
    }

    /// <summary>照会とそのカウンターを一度に解放します。</summary>
    public void Dispose()
    {
        query.Dispose();
        dedicated?.Dispose();
        shared?.Dispose();
    }
}
