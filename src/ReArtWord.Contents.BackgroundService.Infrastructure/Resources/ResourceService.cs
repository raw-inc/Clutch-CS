using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;
using ReArtWord.Contents.BackgroundService.Runtime.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>ワークフローなしでも収集するローカルリソース履歴を提供します。</summary>
public sealed class ResourceService : IResourceService, IDisposable
{
    private readonly ResourceHistory history;
    private readonly string peerId;
    private readonly ResourceOptions options;
    private readonly Guid? instanceId;
    private readonly string? selectedInterfaceId;
    private readonly WindowsGpuProbe? windowsGpu;
    private readonly ResourceHardwareProbe hardware = new();
    private readonly ResourceThroughputProbe throughput = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private ResourceSample? latest;
    private readonly List<ResourceMetric> cpuInformation = [];

    /// <summary>この実体で観測した最新値を、採取や履歴保存を開始せず取得します。</summary>
    public ResourceSample? Latest => Volatile.Read(ref latest);

    /// <summary>共通保存先とPeerローカルの有限履歴方針を固定します。</summary>
    public ResourceService(IDurableRepository repository, string peerId, ResourceOptions options, Guid? instanceId = null, string? selectedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SampleIntervalMilliseconds < 1 || options.HistoryCapacity < 1 || 100000 < options.HistoryCapacity || options.HistoryRetentionHours < 1 || 876000 < options.HistoryRetentionHours)
        {
            throw new ArgumentException("観測間隔と有限履歴の件数・期間を指定してください。", nameof(options));
        }
        this.peerId = peerId;
        this.options = options;
        this.instanceId = instanceId;
        selectedInterfaceId = FindInterface(selectedAddress);
        if (OperatingSystem.IsWindows())
        {
            windowsGpu = WindowsGpuProbe.Create();
        }
        history = new ResourceHistory(repository, "peer:" + peerId + ":resources", options);
    }

    /// <summary>現在値を観測し、操作記録を増やさないローカルスナップショットへ保存します。</summary>
    /// <returns>保存不能でも実測と欠損状態を失わないサンプルです。</returns>
    public ValueTask<ResourceSample> CaptureAsync(CancellationToken cancellationToken = default) => CaptureAsync(true, cancellationToken);

    /// <summary>明示採取と定期採取で診断保存の頻度だけを分けます。</summary>
    /// <returns>保存状態を持つ実測値です。</returns>
    private async ValueTask<ResourceSample> CaptureAsync(bool persist, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            List<ResourceMetric> metrics = [];
            if (cpuInformation.Count == 0)
            {
                cpuInformation.AddRange(await CpuInformation.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
            metrics.AddRange(cpuInformation);
            metrics.AddRange(throughput.Capture());
            try
            {
                metrics.AddRange(hardware.Capture());
                if (OperatingSystem.IsMacOS())
                {
                    metrics.AddRange(await MacResourceProbe.CaptureAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or IOException or UnauthorizedAccessException or System.Net.NetworkInformation.NetworkInformationException)
            {
                metrics.Add(new ResourceMetric("system.observation", "machine", null, "state", "resource.collectionUnavailable"));
            }
            IReadOnlyList<ResourceMetric> nvidia = await NvidiaResourceProbe.CaptureAsync(options.NvidiaSmiPath, cancellationToken).ConfigureAwait(false);
            string usageName = "gpu.usage";
            bool nvidiaOwnsUsage = nvidia.Any(metric => string.Equals(metric.Name, usageName, StringComparison.Ordinal) && metric.Value is double value && double.IsFinite(value));
            if (windowsGpu is not null)
            {
                metrics.AddRange(windowsGpu.Capture(nvidiaOwnsUsage));
                metrics.AddRange(nvidia.Where(metric => nvidiaOwnsUsage || !string.Equals(metric.Name, usageName, StringComparison.Ordinal)));
            }
            else
            {
                metrics.AddRange(nvidia);
            }
            ResourceSample sample = new(Guid.NewGuid(), peerId, observedAt, metrics, InstanceId: instanceId, SelectedInterfaceId: selectedInterfaceId);
            Volatile.Write(ref latest, sample);
            try
            {
                ResourceSample stored = await history.AppendAsync(sample, persist, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref latest, stored);
                return stored;
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return sample;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>保存済みと保存待ちの診断履歴を現在の保持方針内で取得します。</summary>
    /// <returns>警告ワークフローの有無に依存しない履歴です。</returns>
    public ValueTask<IReadOnlyList<ResourceSample>> HistoryAsync(CancellationToken cancellationToken = default) => history.ReadAsync(cancellationToken);

    /// <summary>Host起動中の共通観測をワークフローから独立して継続します。</summary>
    /// <returns>Host終了までの収集処理です。</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(options.SampleIntervalMilliseconds);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                _ = await CaptureAsync(false, cancellationToken).ConfigureAwait(false);

                // 過ぎたtickを取り戻して連続採取せず、必ず次の周期まで非同期で待ちます。
                TimeSpan delay = TimeSpan.FromTicks(interval.Ticks - Stopwatch.GetElapsedTime(started).Ticks % interval.Ticks);
                if (delay < TimeSpan.FromMilliseconds(1))
                {
                    delay = TimeSpan.FromMilliseconds(1);
                }
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            using CancellationTokenSource flush = new(TimeSpan.FromSeconds(5));
            try
            {
                await history.FlushAsync(flush.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
            {
                // 診断保存の失敗でHost停止を妨げず、保存済みの前区間を維持します。
            }
        }
    }

    /// <summary>Hostの収集停止後に排他資源を解放します。</summary>
    public void Dispose()
    {
        gate.Dispose();
        windowsGpu?.Dispose();
        throughput.Dispose();
        history.Dispose();
    }

    /// <summary>指定IPの一意NICを起動時に固定し、後から別NICへ代替しません。</summary>
    /// <returns>確認できたNIC識別子、曖昧または取得不能ならnullです。</returns>
    private static string? FindInterface(string? address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? selected))
        {
            return null;
        }
        try
        {
            string[] matches = NetworkInterface.GetAllNetworkInterfaces().Where(network => network.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(selected))).Select(network => network.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
            return null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
