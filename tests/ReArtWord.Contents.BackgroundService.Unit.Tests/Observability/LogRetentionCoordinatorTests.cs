using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>共有保持の復元前に古い起動値で削除せず、競合中も記録を継続します。</summary>
public sealed class LogRetentionCoordinatorTests
{
    /// <summary>短い起動既定値よりも、保存済みの無期限方針を先に復元します。</summary>
    /// <returns>実ファイルを残した再起動後の確認です。</returns>
    [Fact]
    public async Task RestartPreservesLogsUntilSharedPolicyIsRestoredAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        LogOptions initial = new("peer", fixture.Logs, [new("INFO", 7, null)]);
        LogRetentionSettings desired = LogRetentionCoordinator.FromOptions(initial);
        ClusterOperationalSnapshot shared = Snapshot(desired);
        Guid entryId = Guid.NewGuid();
        WorkflowLogService first = new(initial, fixture.Repository, fixture.Secrets) { RetentionHeld = true };
        await using (first.ConfigureAwait(true))
        {
            using LogRetentionCoordinator coordinator = new(fixture.Repository, first);
            await first.StartAsync(timeout.Token).ConfigureAwait(true);
            await coordinator.ReconcileAsync(shared, timeout.Token).ConfigureAwait(true);
            Assert.False(first.RetentionHeld);
            await first.WriteAsync(new(entryId, DateTimeOffset.UtcNow.AddDays(-10), "INFO", "peer", "retained"), timeout.Token).ConfigureAwait(true);
            await first.FlushAsync(timeout.Token).ConfigureAwait(true);
        }
        LogOptions stale = initial with { Retention = [new("INFO", 0, 0)] };
        WorkflowLogService restarted = new(stale, fixture.Repository, fixture.Secrets) { RetentionHeld = true };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable restartedLifetime = restarted.ConfigureAwait(true);
        using LogRetentionCoordinator restored = new(fixture.Repository, restarted);
        await restarted.StartAsync(timeout.Token).ConfigureAwait(true);
        Assert.Contains((await restarted.QueryAsync(new(), timeout.Token).ConfigureAwait(true)).Entries, item => item.Id == entryId);
        await restored.ReconcileAsync(shared, timeout.Token).ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(desired), restored.Status.AppliedHash);
        Assert.Null(restarted.Options.Retention!.Single(item => string.Equals(item.Level, "INFO", StringComparison.Ordinal)).TotalDays);
        Assert.Contains((await restarted.QueryAsync(new(), timeout.Token).ConfigureAwait(true)).Entries, item => item.Id == entryId);
    }

    /// <summary>保持方針の競合は削除だけを保留し、通常ログは失いません。</summary>
    /// <returns>保持と記録の障害範囲の確認です。</returns>
    [Fact]
    public async Task ConflictingRetentionHoldsMaintenanceButKeepsWritingAsync()
    {
        using LogTestEnvironment fixture = new();
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable logLifetime = log.ConfigureAwait(true);
        using LogRetentionCoordinator coordinator = new(fixture.Repository, log);
        await log.StartAsync().ConfigureAwait(true);
        ClusterOperationalSnapshot shared = Snapshot(LogRetentionCoordinator.FromOptions(log.Options));
        await coordinator.ReconcileAsync(shared).ConfigureAwait(true);
        await coordinator.ReconcileAsync(shared with { UnresolvedFields = [nameof(ClusterOperationalSettings.LogRetention)] }).ConfigureAwait(true);
        Assert.True(log.RetentionHeld);
        Assert.Equal("LogRetention.Conflict", coordinator.Status.Problem);
        Guid id = Guid.NewGuid();
        await log.WriteAsync(new(id, DateTimeOffset.UtcNow, "WARN", "peer", "still recording")).ConfigureAwait(true);
        await log.FlushAsync().ConfigureAwait(true);
        Assert.Contains((await log.QueryAsync(new()).ConfigureAwait(true)).Entries, item => item.Id == id);
        await coordinator.ReconcileAsync(shared).ConfigureAwait(true);
        Assert.False(log.RetentionHeld);
    }

    /// <summary>検証対象の保持方針を含む共通設定を作成します。</summary>
    /// <returns>内容版が一致する設定です。</returns>
    private static ClusterOperationalSnapshot Snapshot(LogRetentionSettings retention)
    {
        ClusterOperationalSettings settings = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), LogRetention: retention);
        return new(settings, ContractJson.Hash(settings), DateTimeOffset.UtcNow);
    }
}
