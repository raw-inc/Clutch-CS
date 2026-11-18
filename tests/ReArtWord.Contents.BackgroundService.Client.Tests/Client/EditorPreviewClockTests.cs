using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ブラウザーの時計差・補正で進捗の寿命やノード診断の新旧を変えません。</summary>
public sealed class EditorPreviewClockTests
{
    /// <summary>一時間先の採取日時でも表示でき、同じ取得IDの再読込みで5分の保持をやり直しません。</summary>
    /// <returns>通信時間・3秒周期・欠測中の期限切れ・不要な再描画を確認します。</returns>
    [Fact]
    public async Task RepeatedPreviewKeepsItsLifetimeAndDoesNotRedrawForAgeOnlyAsync()
    {
        ObservationClock clock = new();
        DateTimeOffset capturedAt = clock.GetUtcNow().AddHours(1);
        NodePreviewSnapshot snapshot = new(Guid.NewGuid(), "workflow", "revision", "progress", "peer", capturedAt, "text/plain", 0)
        {
            Session = new(Guid.NewGuid(), "peer", Guid.NewGuid(), capturedAt), Progress = new(10, 1), CaptureAgeTicks = TimeSpan.FromSeconds(60).Ticks
        };
        int calls = 0;
        bool available = true;
        using ClientHttpHandler handler = new((_, _) =>
        {
            calls++;
            if (!available)
            {
                throw new HttpRequestException("The peer is unavailable.");
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { snapshot }, options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(http, new(clock)) { SelectedClusterId = "cluster" };
        EditorPreviews previews = new(api);
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, clock.GetUtcNow());
        WorkflowDefinition definition = new("workflow", "Workflow", [new("progress", "control.progress", JsonSerializer.SerializeToElement(new { }))], []);
        await previews.RefreshAsync("cluster", definition, [peer]).ConfigureAwait(true);
        Assert.Equal(capturedAt, Assert.Single(previews.Find("cluster", "workflow", "progress")).CapturedAt);
        int version = previews.Version;
        clock.Adjust(TimeSpan.FromHours(-2));
        await previews.RefreshAsync("cluster", definition, [peer]).ConfigureAwait(true);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(3));
        snapshot = snapshot with { CaptureAgeTicks = TimeSpan.FromSeconds(64).Ticks };
        await previews.RefreshAsync("cluster", definition, [peer]).ConfigureAwait(true);
        Assert.Equal(2, calls);
        Assert.Equal(version, previews.Version);
        Assert.Single(previews.Find("cluster", "workflow", "progress"));
        clock.Advance(TimeSpan.FromSeconds(235));
        available = false;
        await previews.RefreshAsync("cluster", definition, [peer]).ConfigureAwait(true);
        Assert.True(previews.Unavailable);
        Assert.Empty(previews.Find("cluster", "workflow", "progress"));
        Assert.True(version < previews.Version);
    }

    /// <summary>同じ実体の古い成功が短い通信経路で届いても、新しい失敗を解除しません。</summary>
    [Fact]
    public void CompletionSequenceWinsOverDifferentRoundTripDurations()
    {
        ObservationClock clock = new();
        Guid instance = Guid.NewGuid();
        NodeExecutionObservation failed = new("workflow", "revision", "node", "peer", EOutcome.Failed, "test.failure", clock.GetUtcNow())
        { ExecutionId = Guid.NewGuid(), SourceInstanceId = instance, CompletionSequence = 2, CompletionAge = new(clock, TimeSpan.FromSeconds(3)) };
        NodeExecutionObservation old = failed with
        { ExecutionId = Guid.NewGuid(), CompletionSequence = 1, CompletionAge = new(clock, TimeSpan.FromSeconds(1)), Outcome = EOutcome.Succeeded };
        EditorDiagnostics diagnostics = new();
        diagnostics.SetHistory("cluster", [old, failed]);
        Assert.Single(diagnostics.Issues("cluster", "workflow"));
        diagnostics.SetHistory("cluster", [old]);
        Assert.Single(diagnostics.Issues("cluster", "workflow"));
        NodeExecutionObservation recovered = old with { CompletionSequence = 3 };
        diagnostics.SetHistory("cluster", [recovered]);
        Assert.Empty(diagnostics.Issues("cluster", "workflow"));
    }
}
