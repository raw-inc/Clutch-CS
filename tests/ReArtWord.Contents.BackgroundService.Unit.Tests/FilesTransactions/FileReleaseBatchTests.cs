using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>解放の件数上限、取消と耐久確認前の応答禁止を検証します。</summary>
public sealed class FileReleaseBatchTests
{
    /// <summary>満杯の組を分けても各要求を一度だけ保存し、保存完了までは応答しません。</summary>
    /// <returns>待機中の取消は除き、残りの全要求を確定した結果です。</returns>
    [Fact]
    public async Task BoundedGroupsCompleteOnlyAfterSavingAsync()
    {
        FileReleaseBatch batch = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<Guid[]> saved = [];
        using CancellationTokenSource cancelled = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        async Task<IReadOnlyList<ClusterResponse>> Apply(Func<IReadOnlyList<FileReleaseBatch.Request>> take, CancellationToken token)
        {
            await release.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            IReadOnlyList<FileReleaseBatch.Request> items = take();
            saved.Add(items.Select(item => item.Id).ToArray());
            return items.Select(_ => new ClusterResponse(EOutcome.Succeeded)).ToArray();
        }
        Task<ClusterResponse>[] tasks = Enumerable.Range(0, 129).Select(index => batch.WriteAsync(Guid.NewGuid(), Release(), Apply,
            index == 10 ? cancelled.Token
                        : timeout.Token)).ToArray();
        try
        {
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
            await cancelled.CancelAsync();
        }
        finally
        {
            release.TrySetResult();
            await ((Task)Task.WhenAll(tasks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tasks[10]);
        Assert.All(await Task.WhenAll(tasks.Where((_, index) => index != 10)), result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        Assert.Equal(3, saved.Count);
        Assert.All(saved, ids => Assert.InRange(ids.Length, 1, 64));
        Assert.Equal(128, saved.SelectMany(ids => ids).Distinct().Count());
    }

    /// <summary>保存失敗を同じ組の全要求へ返し、次の解放を妨げません。</summary>
    /// <returns>失敗の通知漏れと、成功への読み替えがないことを確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureCompletesEveryWaiterAsync(bool takeBeforeFailure)
    {
        FileReleaseBatch batch = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        async Task<IReadOnlyList<ClusterResponse>> Fail(Func<IReadOnlyList<FileReleaseBatch.Request>> take, CancellationToken token)
        {
            await release.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            if (takeBeforeFailure)
            {
                _ = take();
            }
            throw new IOException("The fixture could not confirm the save.");
        }
        Task<ClusterResponse>[] tasks = Enumerable.Range(0, 8).Select(_ => batch.WriteAsync(Guid.NewGuid(), Release(), Fail, timeout.Token)).ToArray();
        release.TrySetResult();
        try
        {
            foreach (Task<ClusterResponse> task in tasks)
            {
                await Assert.ThrowsAsync<IOException>(() => task.WaitAsync(timeout.Token));
            }
        }
        finally
        {
            await ((Task)Task.WhenAll(tasks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        ClusterResponse next = await batch.WriteAsync(Guid.NewGuid(), Release(),
            (take, _) => Task.FromResult<IReadOnlyList<ClusterResponse>>(take().Select(_ => new ClusterResponse(EOutcome.Succeeded)).ToArray()), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
    }

    /// <summary>試験外のPCを参照しない所有情報を作成します。</summary>
    /// <returns>一つの確定済み予約の解放です。</returns>
    private static FileGateRelease Release()
    {
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);
        FileLease lease = new(Guid.NewGuid(), peer.PeerId, peer.InstanceId, peer, [peer], []);
        return new(lease, [peer]);
    }
}
