using System.Net;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>参加表示の寿命をCoreの日時から切り離し、同じ通知で更新しません。</summary>
public sealed class EditorPresenceClockTests
{
    /// <summary>時計差・途中補正・再取得・順序逆転を通して、元通知の表示期間を維持します。</summary>
    /// <returns>HTTP境界の往復時間と、未保存草稿を変更しない確認です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task RepeatedPresenceUsesRemainingLifetimeWithoutChangingTheDraftAsync(int offsetSeconds)
    {
        ObservationClock clock = new();
        PresenceView view = new("remote", Guid.NewGuid(), new("workflow", Guid.NewGuid(), "host", "Other", [], Dirty: true),
            clock.GetUtcNow().AddSeconds(offsetSeconds + 45)) { Sequence = 2, RemainingTicks = TimeSpan.FromSeconds(45).Ticks };
        using ClientHttpHandler handler = new((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { view }, options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        model.Import(new("workflow", "Unsaved workflow", [], []));
        string before = ContractJson.Hash(model.Draft.Document);
        EditorPresence presence = new(model, new(http, new(clock)));
        await presence.RefreshAsync("local").ConfigureAwait(true);
        Assert.Equal(TimeSpan.FromSeconds(44), Assert.Single(presence.Items).Remaining);
        clock.Adjust(TimeSpan.FromDays(2));
        clock.Advance(TimeSpan.FromSeconds(20));
        await presence.RefreshAsync("local").ConfigureAwait(true);
        Assert.InRange(Assert.Single(presence.Items).Remaining!.Value.TotalSeconds, 20, 23);
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Empty(presence.Items);
        for (int pass = 0; pass < 2; pass++)
        {
            await presence.RefreshAsync("local").ConfigureAwait(true);
            Assert.Empty(presence.Items);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        view = view with { Sequence = 3 };
        await presence.RefreshAsync("local").ConfigureAwait(true);
        Assert.Single(presence.Items);
        view = view with { Sequence = 2, Presence = view.Presence with { DisplayName = "Old" } };
        clock.Advance(TimeSpan.FromSeconds(2));
        await presence.RefreshAsync("local").ConfigureAwait(true);
        Assert.Equal("Other", Assert.Single(presence.Items).Presence.DisplayName);
        Assert.True(model.Draft.Dirty);
        Assert.Equal(before, ContractJson.Hash(model.Draft.Document));
    }
}
