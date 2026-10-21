using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実際のRazor描画で、既定の概要と従来の詳細を確認します。</summary>
public sealed class DashboardCombinedSummaryRenderingTests
{
    /// <summary>概要には六つのグラフと最大容量GPUを、詳細には従来の機器情報を表示します。</summary>
    /// <returns>接続や外部プロセスを使わない描画結果の検証です。</returns>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CombinedSummaryShowsTotalsAndDirectionalBreakdownAsync(bool detailed, bool measured)
    {
        Guid instance = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PeerInfo peer = new("peer", instance, "Test workstation", "127.0.0.1", 47740, EPeerStatus.Ready, 100, now, now);
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, now,
        [
            new("cpu.usage", "machine", 25, "percent"), new("memory.usage", "machine", 50, "percent"),
            new("cpu.cores", "machine", 8, "count"), new("cpu.threads", "machine", 16, "count"),
            new("cpu.name", "machine", null, "text", Label: "AMD Ryzen 9 9950X3D 16-Core Processor"),
            new("memory.used", "machine", 8589934592, "bytes"), new("memory.total", "machine", 17179869184, "bytes"),
            new("gpu.usage", "small", 80, "percent", Label: "Small GPU"), new("gpu.memory.total", "small", 2147483648, "bytes", Label: "Small GPU"),
            new("gpu.usage", "large", 20, "percent", Label: "Large GPU"), new("gpu.memory.total", "large", 8589934592, "bytes", Label: "Large GPU"),
            new("gpu.memory.used", "large", 1073741824, "bytes"),
        ], InstanceId: instance, SelectedInterfaceId: "nic");
        if (measured)
        {
            sample = sample with { Metrics = [.. sample.Metrics,
                new("disk.read.rate", "machine", 1048576, "bytes/s"), new("disk.write.rate", "machine", 2097152, "bytes/s"),
                new("network.received.rate", "machine", 50000, "bytes/s"), new("network.sent.rate", "machine", 100000, "bytes/s"),
            ] };
        }
        ServiceCollection services = new();
        services.AddLogging();
        BrowserTestRuntime js = new();
        services.AddSingleton<IJSRuntime>(js);
        services.AddSingleton(new UiText(new(js)));
        using HttpClient http = new() { BaseAddress = new Uri("http://localhost") };
        ReArtWord.Contents.BackgroundService.Client.Services.ManagementApi api = new(http);
        services.AddSingleton(api);
        services.AddSingleton(new ReArtWord.Contents.BackgroundService.Client.Operations.Journal.OperationJournal(api, new(js)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { [nameof(OverviewHostCard.Peers)] = new OverviewPeer[] { new(peer, [sample]) } };
            if (detailed)
            {
                parameters[nameof(OverviewHostCard.Detailed)] = true;
            }
            HtmlRootComponent output = await renderer.RenderComponentAsync<OverviewHostCard>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
        Assert.DoesNotContain("power-problem", html, StringComparison.Ordinal);
        Assert.Contains("Test workstation", html, StringComparison.Ordinal);
        Assert.Contains("Ready", html, StringComparison.Ordinal);
        Assert.Contains("Large GPU", html, StringComparison.Ordinal);
        Assert.Contains("Measured CPU usage", html, StringComparison.Ordinal);
        Assert.Contains("Measured RAM usage", html, StringComparison.Ordinal);
        int graphs = html.Split("role=\"img\"", StringSplitOptions.None).Length - 1;
        if (detailed)
        {
            Assert.Equal(2, graphs);
            Assert.Contains("Small GPU", html, StringComparison.Ordinal);
            Assert.Contains("Selected NICs", html, StringComparison.Ordinal);
            Assert.Contains("Shared system memory", html, StringComparison.Ordinal);
            Assert.Contains("CPU temperature", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(6, graphs);
            Assert.Contains("AMD Ryzen 9 9950X3D 16-Core Processor", html, StringComparison.Ordinal);
            Assert.Contains("8 C / 16 T", html, StringComparison.Ordinal);
            Assert.Contains("8.00 GiB", html, StringComparison.Ordinal);
            Assert.Contains("/ 16.00 GiB · 50.0 %", html, StringComparison.Ordinal);
            Assert.Contains("Measured GPU usage", html, StringComparison.Ordinal);
            Assert.Contains("Measured dedicated GPU memory usage", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Small GPU", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Selected NICs", html, StringComparison.Ordinal);
            Assert.DoesNotContain("CPU temperature", html, StringComparison.Ordinal);
            string[] labels = [">CPU</span>", ">MEM</span>", ">IO</span>", ">GPU</span>", ">VRAM</span>", ">NW</span>"];
            int previous = -1;
            foreach (string label in labels)
            {
                int position = html.IndexOf(label, StringComparison.Ordinal);
                Assert.True(previous < position, "Summary cells must keep their row-major order.");
                previous = position;
            }
            Assert.Equal(1, html.Split(">MiB/s</small>", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, html.Split(">Mbps</small>", StringSplitOptions.None).Length - 1);
            if (measured)
            {
                Assert.Contains(">3.00<small", html, StringComparison.Ordinal);
                Assert.Contains(">1.20<small", html, StringComparison.Ordinal);
                Assert.Contains("Read 1.00 MiB/s", html, StringComparison.Ordinal);
                Assert.Contains("Write 2.00 MiB/s", html, StringComparison.Ordinal);
                Assert.Contains("Down 400.00 Kbps", html, StringComparison.Ordinal);
                Assert.Contains("Up 800.00 Kbps", html, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Read — MiB/s", html, StringComparison.Ordinal);
                Assert.Contains("Write — MiB/s", html, StringComparison.Ordinal);
                Assert.Contains("Down — Mbps", html, StringComparison.Ordinal);
                Assert.Contains("Up — Mbps", html, StringComparison.Ordinal);
            }
            Assert.Contains("Measured total disk traffic", html, StringComparison.Ordinal);
            Assert.Contains("Measured total physical NIC traffic", html, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        }
    }
}
