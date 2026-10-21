using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Updates;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>固定対象の実確認だけが進捗を進め、通信断でも実行中表示を保持します。</summary>
public sealed class FleetUpdateBannerTests
{
    /// <summary>再試行・保留・失敗は成功件数へ加算しません。</summary>
    [Theory]
    [InlineData("Queued", 0)]
    [InlineData("Preparing", 0)]
    [InlineData("Updating", 0)]
    [InlineData("Retrying", 0)]
    [InlineData("Failed", 1)]
    [InlineData("Deferred", 1)]
    [InlineData("Unconfirmed", 1)]
    public void OnlyConfirmedTargetsAdvanceProgress(string state, int attention)
    {
        FleetUpdateActivity summary = FleetUpdateActivity.From(View("Completed", state, "Queued"));
        Assert.Equal(1, summary.Confirmed);
        Assert.Equal(3, summary.Total);
        Assert.Equal(33, summary.Percent);
        Assert.Equal(attention, summary.Attention);
    }

    /// <summary>Coreが完了してもClientHostの更新待ちは100%にしません。</summary>
    [Theory]
    [InlineData(false, EOutcome.Unknown, 1, 0)]
    [InlineData(true, EOutcome.Failed, 1, 1)]
    [InlineData(true, EOutcome.Succeeded, 2, 0)]
    public void ClientHostIsIncludedInTheFixedTotal(bool completed, EOutcome outcome, int confirmed, int attention)
    {
        FleetUpdateView view = View("Completed") with { Client = new("Updating", completed, outcome, null, DateTimeOffset.UtcNow) };
        FleetUpdateActivity summary = FleetUpdateActivity.From(view);
        Assert.Equal(2, summary.Total);
        Assert.Equal(confirmed, summary.Confirmed);
        Assert.Equal(attention, summary.Attention);
        Assert.Equal(confirmed * 50, summary.Percent);
        Assert.Equal(0, FleetUpdateActivity.From(View()).Percent);
    }

    /// <summary>読み取り失敗で直前の値を消さず、復旧後に完了状態を取得します。</summary>
    /// <returns>照会だけで行う失敗と復帰の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReadRetainsProgressAndRecoversAsync(bool invalidJson)
    {
        int calls = 0;
        FleetUpdateView view = View("Completed", "Updating");
        using ClientHttpHandler handler = new((request, cancellation) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/client/updates/visible", request.RequestUri!.AbsolutePath);
            calls++;
            if (calls == 2)
            {
                HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
                if (invalidJson)
                {
                    status = HttpStatusCode.OK;
                }
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("invalid") });
            }
            FleetUpdateView result = view with { Completed = 2 < calls };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FleetUpdateOverview([result], []), options: ContractJson.Options) });
        });
        using HttpClient http = new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        FleetUpdateProgress progress = new(new(http));
        await progress.RefreshAsync().ConfigureAwait(true);
        Assert.False(progress.Unavailable);
        await progress.RefreshAsync().ConfigureAwait(true);
        Assert.True(progress.Unavailable);
        Assert.False(Assert.Single(progress.Updates).Completed);
        Assert.Equal(50, FleetUpdateActivity.From(progress.Updates[0]).Percent);
        await progress.RefreshAsync().ConfigureAwait(true);
        Assert.False(progress.Unavailable);
        Assert.True(Assert.Single(progress.Updates).Completed);
    }

    /// <summary>進捗・対象・保留を描画し、照会不能と完了は別の表示にします。</summary>
    /// <returns>バナーの実描画です。</returns>
    [Fact]
    public async Task BannerShowsCurrentTargetAndHidesCompletedOperationsAsync()
    {
        FleetUpdateView view = View("Completed", "Updating", "Deferred");
        string active = await RenderAsync(view, false).ConfigureAwait(true);
        Assert.Contains("Fleet update in progress", active, StringComparison.Ordinal);
        Assert.Contains("1 of 3 updates confirmed", active, StringComparison.Ordinal);
        Assert.Contains("33%", active, StringComparison.Ordinal);
        Assert.Contains("host-1", active, StringComparison.Ordinal);
        Assert.Contains("1 updates need attention", active, StringComparison.Ordinal);
        Assert.Matches("<progress[^>]*max=\"3\"[^>]*value=\"1\"", active);
        string stale = await RenderAsync(view, true).ConfigureAwait(true);
        Assert.Contains("Release update progress could not be read.", stale, StringComparison.Ordinal);
        Assert.Contains("33%", stale, StringComparison.Ordinal);
        Assert.DoesNotContain("fleet-progress", await RenderAsync(view with { Completed = true }, false).ConfigureAwait(true), StringComparison.Ordinal);
    }

    /// <summary>最大3つの進行中ピアを全て表示し、先頭だけで並列進捗を隠しません。</summary>
    /// <returns>複数対象の描画確認です。</returns>
    [Fact]
    public async Task BannerShowsEveryConcurrentPeerAsync()
    {
        FleetUpdateView view = View("Preparing", "Updating", "Retrying", "Queued");
        Assert.Equal(3, FleetUpdateActivity.From(view).Current.Count);
        string html = await RenderAsync(view, false).ConfigureAwait(true);
        Assert.Contains("host-0", html, StringComparison.Ordinal);
        Assert.Contains("host-1", html, StringComparison.Ordinal);
        Assert.Contains("host-2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("host-3", html, StringComparison.Ordinal);
    }

    /// <summary>同じPC上でも別ピアとして固定された対象を作ります。</summary>
    /// <returns>更新状態の試験値です。</returns>
    private static FleetUpdateView View(params string[] states)
    {
        FleetUpdatePeerProgress[] peers = states.Select((state, index) => new FleetUpdatePeerProgress(
            new("cluster", new("peer-" + index, Guid.NewGuid(), "host-" + index, "127.0.0.1", 47740, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow),
                new("loopback", "127.0.0.1", "127.255.255.255", 47740)), state, [])).ToArray();
        return new(Guid.NewGuid(), "test-release", 3, false, EOutcome.Unknown, peers, DateTimeOffset.UtcNow);
    }

    /// <summary>HTTPや実更新なしで表示の意味を確認します。</summary>
    /// <returns>描画されたHTMLです。</returns>
    private static async Task<string> RenderAsync(FleetUpdateView view, bool unavailable)
    {
        BrowserTestRuntime script = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton(new UiText(new(script)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<FleetUpdateBanner>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FleetUpdateBanner.Updates)] = new[] { FleetUpdateActivity.From(view) },
                [nameof(FleetUpdateBanner.Unavailable)] = unavailable,
            })).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
    }
}
