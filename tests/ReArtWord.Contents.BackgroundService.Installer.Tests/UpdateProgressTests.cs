using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>失敗や切り戻しの途中で、更新の進捗を完了表示にしないことを確認します。</summary>
public sealed class UpdateProgressTests
{
    /// <summary>復帰の応答を待ち、再照会でも進捗を戻さず、最後の転送量を通知します。</summary>
    /// <returns>実際の委譲結果に対応する通知の検証です。</returns>
    [Fact]
    public async Task CompletionWaitsForConfirmedReleaseAfterRollback()
    {
        Guid operation = Guid.NewGuid();
        UpdateTransport transport = new(new(operation, "peer", EUpdatePhase.Starting, EOutcome.Unknown));
        List<OperationProgress> values = [];
        InstallerUpdateProgress progress = new(transport, ["peer"], values.Add);
        await progress.GetPlatformAsync("peer");
        progress.Transfer("peer", 1, 2);
        progress.Transfer("peer", 2, 2);
        Assert.Equal(2, values[^1].CompletedBytes);
        await progress.ReadAsync(operation, "peer");
        transport.Result = transport.Result with { Phase = EUpdatePhase.Failed, Outcome = EOutcome.Failed };
        await progress.ReadAsync(operation, "peer");
        transport.Result = transport.Result with { Phase = EUpdatePhase.RolledBack, Outcome = EOutcome.Succeeded };
        await progress.RollbackAsync(operation, "peer");
        Task release = progress.ReleaseAsync(operation, "peer");
        Assert.False(release.IsCompleted);
        Assert.All(values, value => Assert.True(value.CompletedSteps < value.TotalSteps));
        transport.Release.SetResult();
        await release.ConfigureAwait(true);
        Assert.Equal(values[^1].TotalSteps, values[^1].CompletedSteps);
        Assert.Equal(values.Select(value => value.CompletedSteps).Order(), values.Select(value => value.CompletedSteps));
        Assert.Equal(1, transport.ReleaseCalls);
        Assert.Equal(1, transport.RollbackCalls);
    }

    /// <summary>管理要求の応答だけを差し替え、通知側の再要求も検出します。</summary>
    private sealed class UpdateTransport(UpdatePeerResult result) : IUpdatePeerTransport
    {
        public UpdatePeerResult Result
        {
            get; set;
        } = result;

        public TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReleaseCalls
        {
            get; private set;
        }

        public int RollbackCalls
        {
            get; private set;
        }

        /// <summary>試験対象のプラットフォームを返します。</summary>
        /// <returns>固定したRIDです。</returns>
        public Task<string> GetPlatformAsync(string peerId, CancellationToken cancellationToken = default) => Task.FromResult("win-x64");

        /// <summary>指定した準備結果を返します。</summary>
        /// <returns>試験側の応答です。</returns>
        public Task<UpdatePeerResult> StageAsync(ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default) => Task.FromResult(Result);

        /// <summary>指定した開始結果を返します。</summary>
        /// <returns>試験側の応答です。</returns>
        public Task<UpdatePeerResult> DispatchAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default) => Task.FromResult(Result);

        /// <summary>指定した現在の結果を返します。</summary>
        /// <returns>試験側の応答です。</returns>
        public Task<UpdatePeerResult> ReadAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default) => Task.FromResult(Result);

        /// <summary>切り戻し要求の回数を記録します。</summary>
        /// <returns>指定した復帰結果です。</returns>
        public Task<UpdatePeerResult> RollbackAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.FromResult(Result);
        }

        /// <summary>除外の副作用は行いません。</summary>
        /// <returns>試験用の完了です。</returns>
        public Task ExcludeAsync(string peerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>クラスタ停止の副作用は行いません。</summary>
        /// <returns>試験用の完了です。</returns>
        public Task StopClusterAsync(Guid operationId, IReadOnlyList<string> peerIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>復帰の確認応答まで完了を保留します。</summary>
        /// <returns>試験側が明示した確認完了です。</returns>
        public Task ReleaseAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return Release.Task.WaitAsync(cancellationToken);
        }
    }
}
