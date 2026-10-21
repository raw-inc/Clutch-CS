using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>通常参加と背景補完を混同せず、確認済み件数だけを割合で表示します。</summary>
public sealed class BackgroundHistoryDisplayTests
{
    /// <summary>Readyを残し、完了・未確認状態には背景中の表示を出しません。</summary>
    /// <returns>実Razorの割合・稼働状態・未確認理由の表示です。</returns>
    [Theory]
    [InlineData("Ready", 58, null, true)]
    [InlineData("Ready", 58, "Sync.HistoryUnavailable", true)]
    [InlineData("Ready", 0, null, false)]
    [InlineData("Syncing", 58, null, false)]
    [InlineData("Unconfirmed", 58, null, false)]
    public async Task BackgroundProgressNeverReplacesOperationalState(string state, long pending, string? problem, bool visible)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<PeerStateLabel>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(PeerStateLabel.State)] = state,
            [nameof(PeerStateLabel.History)] = new HistorySynchronizationStatus(pending, problem, 100),
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.Contains(state, html, StringComparison.Ordinal);
        Assert.Equal(visible, html.Contains("role=\"progressbar\"", StringComparison.Ordinal));
        if (visible)
        {
            Assert.Contains("aria-valuenow=\"42\"", html, StringComparison.Ordinal);
            Assert.Contains("42%", html, StringComparison.Ordinal);
            Assert.Equal(problem is not null, html.Contains("Waiting", StringComparison.Ordinal));
        }
    }

    /// <summary>小さいPeerの完了で分母を減らさず、期限切れや別実体の進捗を現状として表示しません。</summary>
    /// <returns>クラスタの件数加重割合と、観測の鮮度境界です。</returns>
    [Fact]
    public async Task AggregateProgressUsesRecordCountsAndMatchingFreshInstances()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PeerInfo first = new("a", Guid.NewGuid(), "host-a", "127.0.0.1", 1234, EPeerStatus.Ready, 1, now, now);
        PeerInfo second = new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 1234, EPeerStatus.Ready, 1, now, now);
        DiscoveredCluster cluster = new("shared", [first, second], [first.PeerId], now);
        DashboardPeerSnapshot Snapshot(PeerInfo peer, HistorySynchronizationStatus history) => new(cluster.ClusterId, peer, now, null,
            JsonSerializer.SerializeToElement(new PeerHealth(cluster.ClusterId, peer.PeerId, peer.InstanceId, EPeerStatus.Ready, null, history), ContractJson.Options), []);
        DashboardPeerSnapshot[] snapshots = [Snapshot(first, new(9, Total: 10)), Snapshot(second, new(0, Total: 40))];
        using ClientHttpHandler handler = new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath switch
            {
                "/client/clusters" => JsonSerializer.Serialize(new[] { cluster }, ContractJson.Options),
                "/client/dashboard/batch" => JsonSerializer.Serialize(snapshots, ContractJson.Options),
                _ => "[]",
            }),
        }));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        AllClustersTelemetry telemetry = new(new ManagementApi(client));
        await telemetry.RefreshAsync();
        Assert.Equal("Ready", telemetry.Health(cluster, now).State);
        Assert.Equal(82, telemetry.Health(cluster, now).History!.ProgressPercent);
        Assert.Null(telemetry.Health(cluster, now.AddSeconds(16)).History);
        Assert.Equal("Unconfirmed", telemetry.Health(cluster with { Peers = [first with { InstanceId = Guid.NewGuid() }, second] }, now).State);
    }
}
