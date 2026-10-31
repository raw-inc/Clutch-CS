using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>概要の実測・実体・取得範囲を捏造せず表示するための検証です。</summary>
public sealed class DashboardOverviewTests
{
    /// <summary>OS累積カウンターの初回・reset・実体変更・時間逆行は速度ゼロと区別します。</summary>
    [Fact]
    public void NetworkRateUsesMatchingInstanceNicAndActualElapsedTime()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid instance = Guid.NewGuid();
        ResourceSample first = Sample(instance, now, 1000);
        ResourceSample next = Sample(instance, now.AddSeconds(3), 4000);
        Assert.Equal(1000d, DashboardMetrics.NetworkRate(first, next, "network.received"));
        Assert.Null(DashboardMetrics.NetworkRate(null, first, "network.received"));
        Assert.Null(DashboardMetrics.NetworkRate(next, Sample(instance, now.AddSeconds(6), 1), "network.received"));
        Assert.Null(DashboardMetrics.NetworkRate(first, next with { InstanceId = Guid.NewGuid() }, "network.received"));
        Assert.Null(DashboardMetrics.NetworkRate(first, next with { SelectedInterfaceId = "other" }, "network.received"));
        Assert.Null(DashboardMetrics.NetworkRate(first, next with { ObservedAt = now }, "network.received"));
        Assert.Equal(0d, DashboardMetrics.NetworkRate(first, Sample(instance, now.AddSeconds(3), 1000), "network.received"));
        Assert.True(DashboardMetrics.IsStale(first, now.AddSeconds(16)));
        Assert.True(DashboardMetrics.IsStale(first with { ObservedAt = now.AddMinutes(1) }, now));
    }

    /// <summary>欠測値を折れ線で埋めず、実測二点未満では線を生成しません。</summary>
    [Fact]
    public void TrendsPreserveMissingIntervals()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid instance = Guid.NewGuid();
        ResourceSample first = Sample(instance, now, 1) with { Metrics = [new("cpu.usage", "machine", 20, "percent")] };
        ResourceSample missing = first with { Id = Guid.NewGuid(), ObservedAt = now.AddSeconds(5), Metrics = [new("cpu.usage", "machine", null, "percent", "unavailable")] };
        ResourceSample last = first with { Id = Guid.NewGuid(), ObservedAt = now.AddSeconds(10), Metrics = [new("cpu.usage", "machine", 30, "percent")] };
        Assert.Empty(DashboardMetrics.Trend([first], "cpu.usage", "machine"));
        Assert.DoesNotContain("L", DashboardMetrics.Trend([first, missing, last], "cpu.usage", "machine"), StringComparison.Ordinal);
        Assert.Contains("L", DashboardMetrics.Trend([first, last], "cpu.usage", "machine"), StringComparison.Ordinal);
    }

    /// <summary>3秒採取を6分保持し、5分の時間軸に正確に配置します。</summary>
    [Fact]
    public void TrendUsesFiveMinutesWithSixMinuteBuffer()
    {
        Guid instance = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResourceSample[] series = [];
        for (int index = 0; index <= 140; index++)
        {
            ResourceSample next = Sample(instance, now.AddSeconds(index * 3), 1) with { Metrics = [new("cpu.usage", "machine", 25, "percent")] };
            series = DashboardSampleHistory.Merge(series, [next], instance, next.ObservedAt);
            series = DashboardSampleHistory.Merge(series, [next], instance, next.ObservedAt);
        }
        Assert.Equal(121, series.Length);
        Assert.Equal(TimeSpan.FromMinutes(6), series[^1].ObservedAt - series[0].ObservedAt);
        string path = DashboardMetrics.Trend(series, "cpu.usage", "machine");
        Assert.StartsWith("M-60.000,75.00", path, StringComparison.Ordinal);
        Assert.Contains("L0.000,75.00", path, StringComparison.Ordinal);
        Assert.EndsWith("L300.000,75.00 ", path, StringComparison.Ordinal);
        Guid restarted = Guid.NewGuid();
        Assert.Single(DashboardSampleHistory.Merge(series, [series[^1] with { InstanceId = restarted }], restarted, series[^1].ObservedAt));
    }

    /// <summary>JavaScriptへ渡すミリ秒原点と折れ線の精度を揃え、同じ点の再配置誤差を防ぎます。</summary>
    [Fact]
    public void TrendRebasePreservesThePositionOfExistingSubsecondSamples()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero).AddTicks(12345);
        Guid instance = Guid.NewGuid();
        ResourceSample first = Sample(instance, now, 1) with { Metrics = [new("cpu.usage", "machine", 25, "percent")] };
        ResourceSample previous = first with { Id = Guid.NewGuid(), ObservedAt = now.AddTicks(30123456) };
        ResourceSample latest = first with { Id = Guid.NewGuid(), ObservedAt = now.AddTicks(60098765) };
        string before = DashboardMetrics.Trend([first, previous], "cpu.usage", "machine");
        string after = DashboardMetrics.Trend([first, previous, latest], "cpu.usage", "machine");
        double firstX = double.Parse(before.AsSpan(1, before.IndexOf(',', StringComparison.Ordinal) - 1), System.Globalization.CultureInfo.InvariantCulture);
        double nextX = double.Parse(after.AsSpan(1, after.IndexOf(',', StringComparison.Ordinal) - 1), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((previous.ObservedAt.ToUnixTimeMilliseconds() - latest.ObservedAt.ToUnixTimeMilliseconds()) / 1000d, nextX - firstX, 9);
    }

    /// <summary>遅れて届いた同じ実体の観測で、確認済みの新しい点を捨てません。</summary>
    [Fact]
    public void LateSampleCannotRewindTheHistoryWindow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid instance = Guid.NewGuid();
        ResourceSample first = Sample(instance, now, 1);
        ResourceSample latest = Sample(instance, now.AddSeconds(6), 3);
        ResourceSample late = Sample(instance, now.AddSeconds(3), 2);
        ResourceSample[] buffer = DashboardSampleHistory.Merge([first, latest], [late], instance, latest.ObservedAt);
        Assert.Equal(new[] { first.Id, late.Id, latest.Id }, buffer.Select(sample => sample.Id));
        Assert.Equal(latest, buffer[^1]);
        ResourceSample expired = Sample(instance, now.AddMinutes(-7), 0);
        Assert.Equal(buffer, DashboardSampleHistory.Merge(buffer, [expired], instance, latest.ObservedAt));
    }

    /// <summary>正常な同一PCの計測元を維持し、未確認になった場合だけ別の実体へ切り替えます。</summary>
    [Fact]
    public void HostMeasurementsKeepTheirHealthySourceWhenAnotherClusterSamplesLater()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid firstInstance = Guid.NewGuid();
        Guid secondInstance = Guid.NewGuid();
        OverviewPeer first = new(new("peer-a", firstInstance, "host", "127.0.0.1", 47740, EPeerStatus.Ready, 1, now, now), [Sample(firstInstance, now, 1)]);
        OverviewPeer later = new(new("peer-b", secondInstance, "host", "127.0.0.1", 47740, EPeerStatus.Ready, 1, now, now), [Sample(secondInstance, now.AddSeconds(1), 2)]);
        static bool Previous(OverviewPeer peer) => peer.Problem is not null;
        Assert.Same(later, DashboardMetrics.HostPeer([first, later], null, Previous));
        Assert.Same(first, DashboardMetrics.HostPeer([first, later], firstInstance, Previous));
        Assert.Same(later, DashboardMetrics.HostPeer([first with { Problem = "unconfirmed" }, later], firstInstance, Previous));
        Assert.Same(later, DashboardMetrics.HostPeer([first, later], secondInstance, Previous));
        Assert.Same(first, DashboardMetrics.HostPeer([first], secondInstance, Previous));
    }

    /// <summary>複数Peerや再取得の同じ原実行IDを一回だけ数え、時刻窓外を除外します。</summary>
    [Fact]
    public void ActivityCountsCompletedOriginalIdsWithinTheObservedWindow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid id = Guid.NewGuid();
        ExecutionResultView current = new(null, new(id, "workflow", "revision", EOutcome.Succeeded, now.AddSeconds(-5), now, [], 0, 0));
        ExecutionResultView old = new(null, new(Guid.NewGuid(), "old", "revision", EOutcome.Failed, now.AddMinutes(-3), now.AddMinutes(-2), [], 0, 0));
        OverviewActivityWindow window = new();
        window.Add([current, old], now);
        window.Add([current], now);
        Assert.Equal(1, Assert.Single(window.Read(now)).Completed);
        Assert.Equal("workflow", Assert.Single(window.Read(now)).WorkflowId);
        Assert.Empty(window.Read(now.AddSeconds(61)));
    }

    /// <summary>Host側の保持上限に達した要約を、全体の活動件数と表示しません。</summary>
    [Fact]
    public void ActivityAtTheHostRetentionLimitIsMarkedAsPartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OverviewActivityWindow window = new();
        ExecutionResultView[] entries = Enumerable.Range(0, 2000).Select(_ => new ExecutionResultView(null,
            new(Guid.NewGuid(), "workflow", "revision", EOutcome.Succeeded, now.AddSeconds(-1), now, [], 0, 0))).ToArray();
        window.Add(entries, now);
        Assert.True(window.CapacityLimited);
        Assert.Equal(2000, Assert.Single(window.Read(now)).Completed);
        window.Add([], now.AddSeconds(61));
        Assert.False(window.CapacityLimited);
        Assert.Empty(window.Read(now.AddSeconds(61)));
    }

    /// <summary>遅れた旧クラスタの応答を新選択へコミットせず、全要求の対象を固定します。</summary>
    /// <returns>実ManagementApiを通した遅延返信の検証です。</returns>
    [Fact]
    public async Task LateReplyCannotPopulateAnotherCluster()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> requested = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            string cluster = Assert.Single(request.Headers.GetValues("X-Contents-Cluster"));
            requested.Add(cluster);
            Guid instance = first;
            string secondCluster = "b";
            if (string.Equals(cluster, secondCluster, StringComparison.Ordinal))
            {
                instance = second;
            }
            string path = request.RequestUri!.AbsolutePath;
            string resourcePath = "/client/dashboard";
            string firstCluster = "a";
            if (string.Equals(path, resourcePath, StringComparison.Ordinal) && string.Equals(cluster, firstCluster, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await finish.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return Response(path, instance, clusterId: cluster);
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "a", SelectedPeerId = "peer" };
        DashboardTelemetry telemetry = new(api);
        Task pending = telemetry.RefreshAsync(Status(first));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            api.SelectedClusterId = "b";
            finish.TrySetResult();
            await pending.ConfigureAwait(true);
            Assert.False(telemetry.IsCurrent);
            Assert.Empty(telemetry.Peers);
            Assert.All(requested, cluster => Assert.Equal("a", cluster));
            await telemetry.RefreshAsync(Status(second));
            Assert.True(telemetry.IsCurrent);
            Assert.Equal(second, Assert.Single(telemetry.Peers).Latest!.InstanceId);
        }
        finally
        {
            finish.TrySetResult();
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>同じPeerが採取直後に再起動した場合は旧実体として集計しません。</summary>
    /// <returns>別Instanceの返信拒否です。</returns>
    [Fact]
    public async Task InstanceChangeDiscardsThePolledSampleAndActivity()
    {
        Guid before = Guid.NewGuid();
        Guid after = Guid.NewGuid();
        using ClientHttpHandler handler = new((request, token) =>
        {
            string statusPath = "/client/dashboard";
            Guid instance = before;
            if (string.Equals(request.RequestUri!.AbsolutePath, statusPath, StringComparison.Ordinal))
            {
                instance = after;
            }
            return Task.FromResult(Response(request.RequestUri.AbsolutePath, instance));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "a", SelectedPeerId = "peer" };
        DashboardTelemetry telemetry = new(api);
        await telemetry.RefreshAsync(Status(before));
        Assert.Null(Assert.Single(telemetry.Peers).Latest);
        Assert.NotNull(Assert.Single(telemetry.Peers).Problem);
        Assert.Equal(0, telemetry.ActivityPeers);
        Assert.True(telemetry.ActivityLimited);
    }

    /// <summary>128Peerの上限を越えるPCも、対象を固定した次ページで取得できます。</summary>
    /// <returns>無制限fan-outを行わない取得範囲です。</returns>
    [Fact]
    public async Task PeerPagingBoundsFanOutWithoutHidingRemainingPeers()
    {
        PeerInfo[] peers = Enumerable.Range(0, 129).Select(index => new PeerInfo("peer" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), Guid.NewGuid(), "host" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1", 1234, EPeerStatus.Ready, 1, null, DateTimeOffset.UtcNow)).ToArray();
        List<string> observed = [];
        using ClientHttpHandler handler = new((request, token) =>
        {
            string peerId = Assert.Single(request.Headers.GetValues("X-Contents-Peer"));
            PeerInfo peer = peers.Single(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal));
            string resourcePath = "/client/dashboard";
            if (string.Equals(request.RequestUri!.AbsolutePath, resourcePath, StringComparison.Ordinal))
            {
                observed.Add(peerId);
            }
            return Task.FromResult(Response(request.RequestUri.AbsolutePath, peer.InstanceId, peerId));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = peers[0].PeerId };
        DashboardTelemetry telemetry = new(api);
        ServiceStatus selected = Status(peers[0].InstanceId) with { PeerId = peers[0].PeerId, Peers = peers };
        await telemetry.RefreshAsync(selected);
        Assert.Equal(128, telemetry.Peers.Count);
        Assert.Equal(128, observed.Distinct(StringComparer.Ordinal).Count());
        Assert.True(telemetry.ActivityLimited);
        telemetry.NextPeers();
        await telemetry.RefreshAsync(selected);
        Assert.Equal(peers[128].PeerId, Assert.Single(telemetry.Peers).Peer.PeerId);
        Assert.Equal(129, observed.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>リソースだけ取得不能でも、検証できた別APIの活動範囲を失いません。</summary>
    /// <returns>独立する読取り結果の検証です。</returns>
    [Fact]
    public async Task ResourceFailureDoesNotDiscardVerifiedExecutionCoverage()
    {
        Guid instance = Guid.NewGuid();
        using ClientHttpHandler handler = new((request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string resourcePath = "/client/dashboard";
            if (string.Equals(path, resourcePath, StringComparison.Ordinal))
            {
                return Task.FromResult(Response(path, instance, resourceMissing: true));
            }
            return Task.FromResult(Response(path, instance));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "a", SelectedPeerId = "peer" };
        DashboardTelemetry telemetry = new(api);
        await telemetry.RefreshAsync(Status(instance));
        Assert.Null(Assert.Single(telemetry.Peers).Latest);
        Assert.NotNull(Assert.Single(telemetry.Peers).Problem);
        Assert.Equal(1, telemetry.ActivityPeers);
        Assert.False(telemetry.ActivityLimited);
    }

    /// <summary>実体とNICを持つ実測例です。</summary>
    /// <returns>速度計算用の入力値です。</returns>
    private static ResourceSample Sample(Guid instance, DateTimeOffset time, double bytes) => new(Guid.NewGuid(), "peer", time, [new("network.received", "nic", bytes, "bytes")], true, instance, "nic");

    /// <summary>当該実体の明示した参加状態です。</summary>
    /// <returns>型付き管理状態です。</returns>
    private static ServiceStatus Status(Guid instance) => new("peer", instance, "peer", EPeerStatus.Ready,
        [new("peer", instance, "host", "127.0.0.1", 1234, EPeerStatus.Ready, 1, null, DateTimeOffset.UtcNow)], [], [], null, JsonSerializer.SerializeToElement(new { }));

    /// <summary>各読取りAPIの型と範囲を保った応答を返します。</summary>
    /// <returns>HTTPで受け取る実JSON形式です。</returns>
    private static HttpResponseMessage Response(string path, Guid instance, string peerId = "peer", bool resourceMissing = false, string clusterId = "a")
    {
        ResourceSample[] samples = [Sample(instance, DateTimeOffset.UtcNow, 100) with { PeerId = peerId }];
        string? problem = null;
        if (resourceMissing)
        {
            samples = [];
            problem = "Resource unavailable";
        }
        object body = path switch
        {
            "/client/dashboard" => new Contracts.Management.Overview.DashboardPeerSnapshot(clusterId, Status(instance).Peers[0] with { PeerId = peerId }, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(Status(instance) with { PeerId = peerId }, ContractJson.Options),
                JsonSerializer.SerializeToElement(new PeerHealth(clusterId, peerId, instance, EPeerStatus.Ready, null), ContractJson.Options),
                samples,
                new ExecutionHistoryPage([], null), new LogQueryResult([], null, true), problem),
            "/api/status" => Status(instance) with { PeerId = peerId },
            "/api/executions/query" => new ExecutionHistoryPage([], null),
            "/api/logs/query" => new LogQueryResult([], null, true),
            _ => throw new InvalidOperationException(path),
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, ContractJson.Options), Encoding.UTF8, "application/json") };
    }
}
