using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.Tests.Client;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>Core・ClientHost・ブラウザーの時計を独立させ、活動と診断の鮮度を確認します。</summary>
public sealed class DashboardExecutionClockTests
{
    private static string Observations => "executions.observations";

    /// <summary>三者の時差と途中補正があっても、通信時間を含めた60秒で表示から外します。</summary>
    /// <returns>実際のJSON契約を通した再読込み・重複除外・表示期限の確認です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task ActivityKeepsItsAgeAcrossCoreHostAndBrowserAsync(int offsetSeconds)
    {
        ObservationClock core = new(), host = new(), browser = new();
        core.Adjust(TimeSpan.FromSeconds(offsetSeconds));
        browser.Adjust(TimeSpan.FromSeconds(-offsetSeconds));
        Guid instance = Guid.NewGuid();
        PeerInfo peer = new("peer", instance, "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, host.GetUtcNow());
        ExecutionResultView view = View(core, EOutcome.Succeeded);
        using DashboardCache cache = new(() => [new DiscoveredCluster("cluster", [peer], [peer.PeerId], host.GetUtcNow())], (command, _) =>
        {
            if (!string.Equals(command.Operation, Observations, StringComparison.Ordinal))
            {
                return Task.FromResult<JsonElement?>(null);
            }
            ExecutionHistoryQuery query = command.Payload!.Json.Deserialize<ExecutionHistoryQuery>(ContractJson.Options)!;
            JsonElement response = JsonSerializer.SerializeToElement(ObservedExecutionHistory.Page([view], query, instance), ContractJson.Options);
            host.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult<JsonElement?>(response);
        }, host);
        await cache.RefreshAsync("cluster", peer, true).ConfigureAwait(true);
        using ClientHttpHandler handler = new(async (_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = JsonContent.Create(cache.Read("cluster", peer.PeerId), options: ContractJson.Options) };
            browser.Advance(TimeSpan.FromSeconds(2));
            return await Task.FromResult(response).ConfigureAwait(false);
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(http, new(browser));
        DashboardPeerSnapshot first = (await api.GetAsync<DashboardPeerSnapshot>("/client/dashboard").ConfigureAwait(true))!;
        ExecutionResultView received = Assert.Single(first.Executions!.Entries);
        Assert.Equal(view.Report!.CompletedAt, received.Summary!.CompletedAt);
        Assert.InRange(received.CompletionAge!.Value.Elapsed.TotalSeconds, 3, 5);
        OverviewActivityWindow window = new(browser);
        window.Add(first.Executions.Entries, api.UtcNow);
        Assert.Equal(1, Assert.Single(window.Read(api.UtcNow)).Completed);
        browser.Adjust(TimeSpan.FromHours(2));
        host.Adjust(TimeSpan.FromHours(-2));
        window.Add(first.Executions.Entries, api.UtcNow);
        Assert.Equal(1, Assert.Single(window.Read(api.UtcNow)).Completed);
        browser.Advance(TimeSpan.FromSeconds(58));
        host.Advance(TimeSpan.FromSeconds(58));
        Assert.Empty(window.Read(api.UtcNow));
        DashboardPeerSnapshot repeated = (await api.GetAsync<DashboardPeerSnapshot>("/client/dashboard").ConfigureAwait(true))!;
        window.Add(repeated.Executions!.Entries, api.UtcNow);
        Assert.Empty(window.Read(api.UtcNow));
    }

    /// <summary>新形式の確認後に欠測しても旧形式へ戻らず、別実体の応答も受け入れません。</summary>
    /// <returns>未確認状態と、再起動後に持ち込まない経過時間です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedSourceNeverFallsBackAfterAnUnconfirmedReadAsync(bool wrongInstance)
    {
        ObservationClock clock = new();
        int legacyCalls = 0;
        Guid instance = Guid.NewGuid();
        ExecutionHistoryPage? page = wrongInstance ? ObservedExecutionHistory.Page([View(clock, EOutcome.Succeeded)], new(), Guid.NewGuid())
                                                   : null;
        ExecutionHistoryPage? result = await DashboardExecutionReader.ReadAsync((operation, _, _) =>
        {
            if (!string.Equals(operation, Observations, StringComparison.Ordinal))
            {
                legacyCalls++;
            }
            return Task.FromResult<JsonElement?>(page is null ? null
                                                              : JsonSerializer.SerializeToElement(page, ContractJson.Options));
        }, instance, true, true, clock).ConfigureAwait(true);
        Assert.Equal(0, legacyCalls);
        Assert.True(result!.AgeUnconfirmed);
        Assert.Empty(result.Entries);
        Assert.Equal(instance, result.ObservationInstanceId);
    }

    /// <summary>日時が逆転しても新しい失敗を優先し、同じ実行の再取得ではノード診断の寿命を延ばしません。</summary>
    [Fact]
    public void NodeDiagnosticsUseCompletionAgeInsteadOfForeignUtc()
    {
        ObservationClock clock = new();
        ExecutionResultView old = View(clock, EOutcome.Succeeded);
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Adjust(TimeSpan.FromHours(-1));
        ExecutionResultView latest = View(clock, EOutcome.Failed);
        IReadOnlyList<NodeExecutionObservation> nodes = DashboardHistory.NodeResults(null, new([old, latest], null), "owner", clock.GetUtcNow());
        NodeExecutionObservation failed = Assert.Single(nodes);
        Assert.Equal(EOutcome.Failed, failed.Outcome);
        Assert.Equal(latest.Report!.Id, failed.ExecutionId);
        EditorDiagnostics diagnostics = new();
        diagnostics.SetHistory("cluster", [failed]);
        Assert.Single(diagnostics.Issues("cluster", "workflow"));
        diagnostics.SetHistory("cluster", [Assert.Single(DashboardHistory.NodeResults(null, new([old], null), "owner", clock.GetUtcNow()))]);
        Assert.Single(diagnostics.Issues("cluster", "workflow"));
        clock.Advance(TimeSpan.FromSeconds(301));
        clock.Adjust(TimeSpan.FromHours(2));
        Assert.Empty(DashboardHistory.NodeResults(nodes, new([old, latest], null), "owner", clock.GetUtcNow()));
    }

    /// <summary>活動上限の注意表示も、PC時刻の補正で解除したり無期限に残したりしません。</summary>
    [Fact]
    public void CapacityWarningUsesElapsedTime()
    {
        ObservationClock clock = new();
        OverviewActivityWindow window = new(clock);
        window.Add(Enumerable.Range(0, 2000).Select(_ => View(clock, EOutcome.Succeeded)).ToArray(), clock.GetUtcNow());
        Assert.True(window.CapacityLimited);
        clock.Adjust(TimeSpan.FromDays(1));
        window.Add([], clock.GetUtcNow());
        Assert.True(window.CapacityLimited);
        clock.Adjust(TimeSpan.FromDays(-2));
        clock.Advance(TimeSpan.FromSeconds(61));
        window.Add([], clock.GetUtcNow());
        Assert.False(window.CapacityLimited);
        Assert.Empty(window.Read(clock.GetUtcNow()));
    }

    /// <summary>同じ実行先のノード成否と、採取元で測る完了年齢を用意します。</summary>
    /// <returns>出力本文を持たない小さい結果です。</returns>
    private static ExecutionResultView View(ObservationClock clock, EOutcome outcome) => new(new(Guid.NewGuid(), "workflow", "revision", outcome,
        clock.GetUtcNow(), clock.GetUtcNow(), [new("node", outcome, [new("peer", NodeResult.Success() with { Outcome = outcome, ReasonCode = "test.result" })])]))
    { CompletionAge = new(clock), CompletionOrder = clock.GetTimestamp() + 1 };
}
