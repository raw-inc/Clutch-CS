using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Overview.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>別ClientHostの進捗と、全画面共通の小さな更新表示を検証します。</summary>
public sealed class FleetVisibilityTests
{
    /// <summary>操作IDを重複計数せず、自身の保存結果を優先します。</summary>
    /// <returns>画面で共有する進捗の検証です。</returns>
    [Fact]
    public async Task SharedProgressUsesOwnedResultsAndTracksRemoteFreshnessAsync()
    {
        FleetUpdateView own = new(Guid.NewGuid(), "owned", 2, false, EOutcome.Unknown, [], DateTimeOffset.UtcNow);
        FleetUpdateActivity remote = Activity();
        FleetUpdateOverview overview = new([own], [new(Guid.NewGuid(), 1, FleetUpdateActivity.From(own) with { Completed = true }, true), new(Guid.NewGuid(), 2, remote, true)]);
        using ClientHttpHandler handler = new((request, cancellation) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(overview, options: ContractJson.Options) }));
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        FleetUpdateProgress progress = new(new(http));
        await progress.RefreshAsync().ConfigureAwait(true);
        Assert.Single(progress.Updates);
        Assert.Equal(2, progress.Activities.Count);
        Assert.False(progress.Activities.Single(item => item.OperationId == own.OperationId).Completed);
        Assert.Equal(remote.OperationId, Assert.Single(progress.StaleOperations));
    }

    /// <summary>旧ClientHostの段階更新中も従来の所有進捗へフォールバックします。</summary>
    /// <returns>旧APIとの互換性の検証です。</returns>
    [Fact]
    public async Task OlderHostFallsBackToItsOwnedProgressAsync()
    {
        int calls = 0;
        using ClientHttpHandler handler = new((request, cancellation) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("/client/updates/visible", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            Assert.Equal("/client/updates", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<FleetUpdateView>(), options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        FleetUpdateProgress progress = new(new(http));
        await progress.RefreshAsync().ConfigureAwait(true);
        Assert.False(progress.Unavailable);
        Assert.Equal(2, calls);
    }

    /// <summary>フッターは詳細なしの割合を表示し、観測停止時は不定進捗にします。</summary>
    /// <returns>実コンポーネントの描画検証です。</returns>
    [Fact]
    public async Task FooterShowsOnlyActiveProgressAndDoesNotClaimStalePercentAsync()
    {
        FleetUpdateActivity activity = Activity();
        string active = await RenderAsync<FleetUpdateFooter>(activity, false).ConfigureAwait(true);
        Assert.Contains("Updating", active, StringComparison.Ordinal);
        Assert.Contains("50%", active, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", active, StringComparison.Ordinal);
        Assert.Matches("<progress[^>]*value=\"1\"", active);
        string stale = await RenderAsync<FleetUpdateFooter>(activity, true).ConfigureAwait(true);
        Assert.Contains("Update status unconfirmed", stale, StringComparison.Ordinal);
        Assert.DoesNotContain("50%", stale, StringComparison.Ordinal);
        Assert.DoesNotMatch("<progress[^>]*value=", stale);
        Assert.DoesNotContain("fleet-footer", await RenderAsync<FleetUpdateFooter>(activity with { Completed = true }, false).ConfigureAwait(true), StringComparison.Ordinal);
        string banner = await RenderAsync<FleetUpdateBanner>(activity, false).ConfigureAwait(true);
        Assert.Contains("private-host", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", banner, StringComparison.Ordinal);
    }

    /// <summary>二対象のうち一つが更新済みの外部操作です。</summary>
    /// <returns>フッターとDashboardに共通の進捗です。</returns>
    private static FleetUpdateActivity Activity() => new(Guid.NewGuid(), "remote", false, EOutcome.Unknown, 1, 2, 0, DateTimeOffset.UtcNow, [new("cluster", "peer", "private-host", "Updating")]);

    /// <summary>ローカライズを含む実コンポーネントのHTMLを取得します。</summary>
    /// <returns>描画されたHTMLです。</returns>
    private static async Task<string> RenderAsync<T>(FleetUpdateActivity activity, bool stale) where T : IComponent
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
            Guid[] staleOperations = [];
            if (stale)
            {
                staleOperations = [activity.OperationId];
            }
            Dictionary<string, object?> parameters = new()
            {
                [nameof(FleetUpdateFooter.Updates)] = new[] { activity },
                [nameof(FleetUpdateFooter.StaleOperations)] = staleOperations,
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
    }
}
