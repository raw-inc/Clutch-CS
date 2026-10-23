using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>停止中のhost、保存順序、再試行と復帰を外から観測する配送境界です。</summary>
internal sealed class FleetUpdateTestRemote : IFleetUpdateRemote
{
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<(string Cluster, string Peer), FleetPeerObservation> Observations = [];
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = [];
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, UpdatePeerResult> Results = [];
    internal int StageFailures;
    internal bool StageProtocolFailure;
    internal string? FailureReason;
    internal int Disposals;
    internal Func<int, EUpdatePhase> DispatchPhase = _ => EUpdatePhase.Completed;
    internal Action<FleetUpdateTarget>? BeforeStage;
    internal Action? BeforeDispatch;
    internal Func<FleetUpdateTarget, CancellationToken, Task>? AfterDispatch;
    private int dispatches;

    /// <summary>試験対象を稼働中として初期化します。</summary>
    internal FleetUpdateTestRemote(IEnumerable<FleetUpdateTarget> targets)
    {
        foreach (FleetUpdateTarget target in targets)
        {
            Observations[(target.ClusterId, target.Peer.PeerId)] = new(target.Peer, true, "old");
        }
    }

    /// <summary>同じhostの現在状態を返します。</summary>
    /// <returns>試験で観測しているhealthです。</returns>
    public Task<FleetPeerObservation?> ObserveAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default)
        => Task.FromResult<FleetPeerObservation?>(Observations.GetValueOrDefault((target.ClusterId, target.Peer.PeerId)));

    /// <summary>停止中のピアも含め、同じクラスタの所属IDを返します。</summary>
    /// <returns>試験で保持する所属集合です。</returns>
    public Task<IReadOnlyList<PeerInfo>?> MembersAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PeerInfo>?>(Observations.Where(item => string.Equals(item.Key.Cluster, target.ClusterId, StringComparison.Ordinal)).Select(item => item.Value.Peer).ToArray());

    /// <summary>一試行だけの配送を作ります。</summary>
    /// <returns>解放も計数する試験用配送です。</returns>
    public IUpdatePeerTransport Create(ClusterUpdateRequest request, FleetUpdateTarget target) => new Transport(this, request, target);

    /// <summary>一試行の固定入力と寿命を保持します。</summary>
    private sealed class Transport(FleetUpdateTestRemote owner, ClusterUpdateRequest request, FleetUpdateTarget target) : IUpdatePeerTransport, IAsyncDisposable
    {
        /// <summary>対象RIDを返します。</summary>
        /// <returns>固定リリースのRIDです。</returns>
        public Task<string> GetPlatformAsync(string peerId, CancellationToken cancellationToken = default) => Task.FromResult(request.Manifest.Platform);

        /// <summary>副作用前の転送障害を再現します。</summary>
        /// <returns>旧リリースを確認した準備状態です。</returns>
        public Task<UpdatePeerResult> StageAsync(ClusterUpdateRequest selected, string peerId, CancellationToken cancellationToken = default)
        {
            owner.Events.Enqueue("stage:" + target.Peer.HostId + "/" + target.ClusterId);
            owner.BeforeStage?.Invoke(target);
            if (0 <= Interlocked.Decrement(ref owner.StageFailures))
            {
                if (owner.StageProtocolFailure)
                {
                    throw new UpdateTransportException("seal", EOutcome.Unknown);
                }
                throw new IOException("Transfer failed.");
            }
            return Task.FromResult(new UpdatePeerResult(request.OperationId, peerId, EUpdatePhase.Prepared, EOutcome.Succeeded, "old",
                InstanceId: target.Peer.InstanceId, PreparedInstanceId: target.Peer.InstanceId));
        }

        /// <summary>停止と再起動の間は通常処理不能にします。</summary>
        /// <returns>指定した終端結果です。</returns>
        public async Task<UpdatePeerResult> DispatchAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            owner.BeforeDispatch?.Invoke();
            owner.Events.Enqueue("dispatch:" + target.Peer.HostId + "/" + target.ClusterId);
            EUpdatePhase phase = owner.DispatchPhase(Interlocked.Increment(ref owner.dispatches));
            FleetPeerObservation old = owner.Observations[(target.ClusterId, peerId)];
            string version = "new";
            if (phase is EUpdatePhase.RolledBack or EUpdatePhase.Failed)
            {
                version = "old";
            }
            FleetPeerObservation current = new(old.Peer with { InstanceId = Guid.NewGuid() }, false, version);
            owner.Observations[(target.ClusterId, peerId)] = current;
            EOutcome outcome = phase switch
            {
                EUpdatePhase.Completed => EOutcome.Succeeded,
                EUpdatePhase.RolledBack => EOutcome.Failed,
                EUpdatePhase.Failed => EOutcome.Failed,
                _ => EOutcome.Unknown
            };
            UpdatePeerResult result = new(operationId, peerId, phase, outcome, version, Reason: owner.FailureReason, InstanceId: current.Peer.InstanceId);
            owner.Results[operationId] = result;
            if (owner.AfterDispatch is not null)
            {
                await owner.AfterDispatch(target, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>元操作の保存結果だけを返します。</summary>
        /// <returns>再実行しない照会です。</returns>
        public Task<UpdatePeerResult> ReadAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
            => Task.FromResult(owner.Results[operationId]);

        /// <summary>保留解除後だけ通常処理へ戻します。</summary>
        /// <returns>解除完了です。</returns>
        public Task ReleaseAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            owner.Events.Enqueue("release:" + target.Peer.HostId + "/" + target.ClusterId);
            owner.Observations[(target.ClusterId, peerId)] = owner.Observations[(target.ClusterId, peerId)] with { Ready = true };
            return Task.CompletedTask;
        }

        /// <summary>全クラスタ停止が呼ばれたら試験を失敗させます。</summary>
        /// <returns>この経路は使用しません。</returns>
        public Task StopClusterAsync(Guid operationId, IReadOnlyList<string> peerIds, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Whole-cluster maintenance is forbidden.");

        /// <summary>追加の復元操作を許しません。</summary>
        /// <returns>この経路は使用しません。</returns>
        public Task<UpdatePeerResult> RollbackAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No extra rollback.");

        /// <summary>無関係なピアを除外しません。</summary>
        /// <returns>この経路は使用しません。</returns>
        public Task ExcludeAsync(string peerId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No implicit exclusion.");

        /// <summary>生成した全配送の解放を確認します。</summary>
        /// <returns>解放完了です。</returns>
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.Disposals);
            return ValueTask.CompletedTask;
        }
    }
}
