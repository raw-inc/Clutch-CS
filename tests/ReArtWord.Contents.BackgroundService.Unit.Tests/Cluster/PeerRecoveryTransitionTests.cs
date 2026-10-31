using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>準備待ちと人のローカル遷移が永続マーカーを逆順に上書きしないことを確認します。</summary>
public sealed class PeerRecoveryTransitionTests
{
    /// <summary>準備確認中に試験が失敗しても、初期化の終了を待ってから資源を破棄します。</summary>
    /// <returns>元の例外と、準備確認・保存処理の終了です。</returns>
    [Fact]
    public async Task FailedAssertionFinishesReadinessBeforeDisposalAsync()
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        ClusterOptions options = new(id, "one", "host", "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath());
        PeerMembership membership = new(options, Guid.NewGuid(), TimeProvider.System);
        using PeerRecovery recovery = new(options, repository, membership);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery.ReadinessCheck = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return true;
        };
        Task starting = recovery.InitializeAsync(cancellation.Token);
        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable startingLifetime = new TestTaskLifetime(starting, cancellation).ConfigureAwait(true);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                throw new InvalidOperationException("fixture assertion failed");
            }).ConfigureAwait(true);
            Assert.Equal("fixture assertion failed", failure.Message);
            Assert.True(starting.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            try
            {
                await starting.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 検証で例外が発生した場合も、初期化を回収してから資源を破棄します。
            }
        }
    }

    /// <summary>解消済みの内容分岐は実同期へ戻し、識別衝突と人の離脱は維持します。</summary>
    /// <returns>未確認のままReadyへ昇格しない復旧遷移です。</returns>
    [Theory]
    [InlineData(false, false, EPeerStatus.Syncing)]
    [InlineData(true, false, EPeerStatus.Conflict)]
    [InlineData(false, true, EPeerStatus.Detached)]
    public async Task ResolvedContentConflictReturnsToSynchronization(bool identityConflict, bool detached, EPeerStatus expected)
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        ClusterOptions options = new(id, "one", "host", "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath());
        PeerMembership membership = new(options, Guid.NewGuid(), TimeProvider.System);
        using PeerRecovery recovery = new(options, repository, membership) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await recovery.InitializeAsync();
        if (identityConflict)
        {
            PeerInfo duplicate = Assert.Single(membership.Snapshot()) with { InstanceId = Guid.NewGuid(), ReadySince = DateTimeOffset.UtcNow.AddDays(-1) };
            Assert.False(membership.Observe(new(duplicate, "different", null, null, 1), duplicate.Address));
            Assert.True(membership.IdentityConflict);
        }
        membership.SetLocal(EPeerStatus.Conflict);
        if (detached)
        {
            await recovery.DetachAsync();
        }
        await recovery.CheckAvailabilityAsync();
        Assert.Equal(expected, Assert.Single(membership.Snapshot()).Status);
        Assert.Empty(await repository.GetConflictsAsync(string.Empty));
    }

    /// <summary>準備確認の待機中に受け付けた離脱を、後続の通常参加保存で失いません。</summary>
    /// <returns>実SQLiteの再読と新しい実体での復旧結果です。</returns>
    [Fact]
    public async Task DetachDuringReadinessWaitRemainsDetachedAfterRestart()
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        ClusterOptions options = new(id, "one", "host", "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath());
        PeerMembership membership = new(options, Guid.NewGuid(), TimeProvider.System);
        using PeerRecovery recovery = new(options, repository, membership);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery.ReadinessCheck = async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task starting = recovery.InitializeAsync(timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable startingLifetime = new TestTaskLifetime(starting, timeout).ConfigureAwait(true);
        await entered.Task.WaitAsync(timeout.Token);
        Task detach = recovery.DetachAsync(timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable detachLifetime = new TestTaskLifetime(detach, timeout).ConfigureAwait(true);
        Task laterReady = recovery.BecomeReadyAsync(timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readyLifetime = new TestTaskLifetime(laterReady, timeout).ConfigureAwait(true);
        bool detachedBeforeReadinessCompleted = detach.IsCompleted;
        finish.TrySetResult();
        await Task.WhenAll(starting, detach, laterReady).WaitAsync(timeout.Token);
        Assert.False(detachedBeforeReadinessCompleted);
        Assert.True(recovery.Detached);
        Assert.Equal(EPeerStatus.Detached, Assert.Single(membership.Snapshot()).Status);
        Assert.Equal("detached", (await repository.ReadAsync("cluster:" + id, "one"))!.Value!.GetString());
        PeerMembership restartedMembership = new(options, Guid.NewGuid(), TimeProvider.System);
        using PeerRecovery restarted = new(options, repository, restartedMembership) { ReadinessCheck = token => ValueTask.FromResult(true) };
        await restarted.InitializeAsync(timeout.Token);
        await restarted.CheckAvailabilityAsync(timeout.Token);
        Assert.True(restarted.Detached);
        Assert.Equal(EPeerStatus.Detached, Assert.Single(restartedMembership.Snapshot()).Status);
    }

    /// <summary>複数の同時準備要求を一つの初回マーカー保存へ直列化します。</summary>
    /// <returns>CAS敗退と同値操作記録増加がないことです。</returns>
    [Fact]
    public async Task ConcurrentReadinessWritesTrustedMarkerOnce()
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        ClusterOptions options = new(id, "one", "host", "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath());
        PeerMembership membership = new(options, Guid.NewGuid(), TimeProvider.System);
        using PeerRecovery recovery = new(options, repository, membership) { ReadinessCheck = token => ValueTask.FromResult(false) };
        await recovery.InitializeAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery.ReadinessCheck = async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task[] pending = Enumerable.Range(0, 8).Select(_ => recovery.BecomeReadyAsync(timeout.Token)).ToArray();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable pendingLifetime = new TestTaskLifetime(Task.WhenAll(pending), timeout).ConfigureAwait(true);
        await entered.Task.WaitAsync(timeout.Token);
        finish.TrySetResult();
        await Task.WhenAll(pending).WaitAsync(timeout.Token);
        string scope = "cluster:" + id;
        DurableEntry marker = (await repository.ReadAsync(scope, "one"))!;
        Assert.Equal("trusted", marker.Value!.GetString());
        Assert.Null(marker.ParentVersion);
        await recovery.CheckAvailabilityAsync(timeout.Token);
        Assert.Equal(marker.Version, (await repository.ReadAsync(scope, "one"))!.Version);
    }
}
