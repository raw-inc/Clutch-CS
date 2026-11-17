using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Tests.Client;
using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>CoreとClientHostの日時補正を、リソースと稼働確認の有効期間から切り離します。</summary>
public sealed class DashboardObservationAgeTests
{
    /// <summary>診断照会の遅延や失敗が、以前に確認したhealth・計測の起点を更新しません。</summary>
    /// <returns>時計補正後も同じ確認事実を再配信することの確認です。</returns>
    [Fact]
    public async Task SlowDiagnosticsDoNotRenewTheConfirmationAsync()
    {
        ObservationClock clock = new();
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 0, clock.GetUtcNow(), clock.GetUtcNow());
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, clock.GetUtcNow(), [], InstanceId: peer.InstanceId);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())], async (command, token) =>
        {
            string logs = "logs.query", executions = "executions.list", current = "dashboard.current";
            if (string.Equals(command.Operation, logs, StringComparison.Ordinal) || string.Equals(command.Operation, executions, StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token).ConfigureAwait(true);
                throw new IOException("Diagnostic read failed.");
            }
            if (string.Equals(command.Operation, current, StringComparison.Ordinal))
            {
                return Bundle(peer, sample, clock.GetUtcNow(), 0);
            }
            return null;
        }, clock);
        Task diagnostics = cache.RefreshAsync("cluster", peer, true);
        try
        {
            await cache.RefreshAsync("cluster", peer, false).ConfigureAwait(true);
            DashboardPeerSnapshot first = cache.Read("cluster", peer.PeerId)!;
            Assert.NotNull(first.Health);
            Assert.Single(first.Samples);
            Assert.False(diagnostics.IsCompleted);
            clock.Adjust(TimeSpan.FromHours(-1));
            release.SetResult();
            await diagnostics.ConfigureAwait(true);
            DashboardPeerSnapshot after = cache.Read("cluster", peer.PeerId)!;
            Assert.Equal(first.ConfirmationAge, after.ConfirmationAge);
            Assert.Equal(first.CheckedAt!.Value.AddHours(-1), after.CheckedAt);
            Assert.NotNull(after.Health);
            Assert.Equal(first.Samples[0].Id, Assert.Single(after.Samples).Id);
            Assert.Equal(TimeSpan.Zero, clock.GetUtcNow() - after.Samples[0].ObservedAt);
            Assert.Null(after.Logs);
        }
        finally
        {
            release.TrySetResult();
            await diagnostics.ConfigureAwait(true);
        }
    }

    /// <summary>通信中の時間を含め、同じ標本を日時補正や再取得で新しく見せません。</summary>
    /// <returns>キャッシュの生存期間と、再配信時の表示用日時です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task ClockCorrectionKeepsElapsedResourceAndHealthAgesAsync(int correction)
    {
        ObservationClock clock = new();
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 0, clock.GetUtcNow(), clock.GetUtcNow());
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, clock.GetUtcNow(), [], InstanceId: peer.InstanceId);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())], (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult<JsonElement?>(Bundle(peer, sample, sample.ObservedAt.AddHours(1), TimeSpan.FromSeconds(3).Ticks));
        }, clock);
        await cache.RefreshAsync("cluster", peer, false).ConfigureAwait(true);
        ResourceSample received = Assert.Single(cache.Read("cluster", peer.PeerId)!.Samples);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.GetUtcNow() - received.ObservedAt);
        clock.Adjust(TimeSpan.FromSeconds(correction));
        DashboardPeerSnapshot adjusted = cache.Read("cluster", peer.PeerId)!;
        Assert.NotNull(adjusted.Health);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.GetUtcNow() - Assert.Single(adjusted.Samples).ObservedAt);
        Assert.False(DashboardMetrics.IsStale(adjusted.Samples[0], clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(11));
        await cache.RefreshAsync("cluster", peer, false).ConfigureAwait(true);
        ResourceSample repeated = Assert.Single(cache.Read("cluster", peer.PeerId)!.Samples);
        Assert.Equal(TimeSpan.FromSeconds(18), clock.GetUtcNow() - repeated.ObservedAt);
        Assert.True(DashboardMetrics.IsStale(repeated, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(7));
        DashboardPeerSnapshot expired = cache.Read("cluster", peer.PeerId)!;
        Assert.Null(expired.Health);
        Assert.Empty(expired.Samples);
    }

    /// <summary>不正な経過時間を受け取っても、日時から都合のよい鮮度を推定し直しません。</summary>
    /// <returns>計測値だけを確認不能として分離した結果です。</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task InvalidAgeDoesNotFallBackToSourceUtcAsync(long ticks)
    {
        ObservationClock clock = new();
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 0, clock.GetUtcNow(), clock.GetUtcNow());
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, clock.GetUtcNow(), [], InstanceId: peer.InstanceId);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())],
            (_, _) => Task.FromResult<JsonElement?>(Bundle(peer, sample, clock.GetUtcNow(), ticks)), clock);
        await cache.RefreshAsync("cluster", peer, false).ConfigureAwait(true);
        DashboardPeerSnapshot snapshot = cache.Read("cluster", peer.PeerId)!;
        Assert.NotNull(snapshot.Health);
        Assert.Empty(snapshot.Samples);
        Assert.NotNull(snapshot.Problem);
    }

    /// <summary>元の採取日時と、採取元で計測した経過時間を別項目で返します。</summary>
    /// <returns>Coreの一括照会と同じ構造です。</returns>
    private static JsonElement Bundle(PeerInfo peer, ResourceSample sample, DateTimeOffset sourceNow, long age)
    {
        var health = new { clusterId = "cluster", peerId = peer.PeerId, instanceId = peer.InstanceId, status = EPeerStatus.Ready };
        return JsonSerializer.SerializeToElement(new { health, status = health, resource = sample, observedAt = sourceNow, resourceAgeTicks = age }, ContractJson.Options);
    }
}
