using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core間で保持方針を共有し、停止中変更と再参加を確認します。</summary>
public sealed class LogRetentionClusterTests
{
    /// <summary>別Peerの保存先を変えず共有方針だけを適用し、再参加後も最新を使用します。</summary>
    /// <returns>実プロセスとUDPによる検証完了です。</returns>
    [Fact]
    public async Task SharedRetentionSurvivesRestartAndRejoinAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            PeerInfo[] peers = status.GetProperty("peers").Deserialize<PeerInfo[]>(ContractJson.Options)!;
            LogRetentionView a = await first.GetAsync<LogRetentionView>("logs.retention", token).ConfigureAwait(true);
            LogRetentionView b = await second.GetAsync<LogRetentionView>("logs.retention", token).ConfigureAwait(true);
            return peers.Count(peer => peer.Status == EPeerStatus.Ready) == 2
                && a.Local?.AppliedHash is not null && string.Equals(a.Local.AppliedHash, b.Local?.AppliedHash, StringComparison.Ordinal);
        }).ConfigureAwait(true);
        LogOptions firstOptions = await first.GetAsync<LogOptions>("logs.options").ConfigureAwait(true);
        LogOptions secondOptions = await second.GetAsync<LogOptions>("logs.options").ConfigureAwait(true);
        LogRetentionView state = await first.GetAsync<LogRetentionView>("logs.retention").ConfigureAwait(true);
        LogOptions changed = firstOptions with { Retention = firstOptions.Retention!.Select(rule => rule with { TotalDays = 90 }).ToArray(), RollingTime = "04:00" };
        LogOptionsChangeRequest request = new(changed, OperationId: Guid.NewGuid(), ExpectedRevision: state.Configuration.Saved!.Revision);
        ClusterOperationalSaveResult result = await first.PostAsync<ClusterOperationalSaveResult>("logs.change-options", request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Shared.Outcome);
        Assert.True(result.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        ClusterOperationalSaveResult replay = await first.PostAsync<ClusterOperationalSaveResult>("logs.change-options", request).ConfigureAwait(true);
        Assert.Equal(result.Revision, replay.Revision);
        ClusterOperationalSaveResult different = await first.PostAsync<ClusterOperationalSaveResult>("logs.change-options", request with { ApplyToExisting = !request.ApplyToExisting }).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, different.Saved.Outcome);
        await AppliedAsync(second, "04:00").ConfigureAwait(true);
        Assert.Equal(secondOptions.Directory, (await second.GetAsync<LogOptions>("logs.options").ConfigureAwait(true)).Directory);
        await second.StopAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            PeerInfo[] peers = status.GetProperty("peers").Deserialize<PeerInfo[]>(ContractJson.Options)!;
            return peers.All(peer => !string.Equals(peer.PeerId, "b", StringComparison.Ordinal) || !PeerStatusRules.CanParticipate(peer.Status));
        }).ConfigureAwait(true);
        state = await first.GetAsync<LogRetentionView>("logs.retention").ConfigureAwait(true);
        result = await first.PostAsync<ClusterOperationalSaveResult>("logs.change-options", new LogOptionsChangeRequest(changed with { RollingTime = "05:00" },
            OperationId: Guid.NewGuid(), ExpectedRevision: state.Configuration.Saved!.Revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Saved.Outcome);
        Assert.True(result.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        await second.StartAsync().ConfigureAwait(true);
        await AppliedAsync(second, "05:00").ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await AppliedAsync(first, "05:00").ConfigureAwait(true);
    }

    /// <summary>実反映と全レベルの保持値を確認します。</summary>
    /// <returns>反映完了までの待機です。</returns>
    private static Task AppliedAsync(CorePeer peer, string rolling, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token =>
        {
            LogRetentionView view = await peer.GetAsync<LogRetentionView>("logs.retention", token).ConfigureAwait(true);
            return view.Local is { MaintenanceHeld: false, Problem: null, Applied: { } applied }
                && string.Equals(applied.RollingTime, rolling, StringComparison.Ordinal) && applied.Rules.All(rule => rule.TotalDays == 90);
        }, cancellationToken);
}
