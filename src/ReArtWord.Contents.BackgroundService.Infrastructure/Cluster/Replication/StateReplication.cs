using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>共有CASだけを現在Coordinatorへ明示配送します。</summary>
public sealed class StateReplication : IDisposable
{
    private readonly PeerCluster cluster;
    private readonly SemaphoreSlim updates = new(1, 1);
    private readonly IDurableRepository repository;

    /// <summary>配送処理を一度登録し、状態保存へ接続します。</summary>
    public StateReplication(PeerCluster cluster, IDurableRepository repository, NodeStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(stateStore);
        this.cluster = cluster;
        this.repository = repository;
        cluster.RegisterHandler("state.cas", ApplyAsync);
        SharedDedupeReservations reservations = new(cluster, repository, stateStore, CompareAsync);
        cluster.RegisterHandler(SharedDedupeReservations.Kind, reservations.HandleAsync);
        stateStore.ReserveShared = reservations.ReserveAsync;
        stateStore.SharedWriter = WriteAsync;
        stateStore.BindDetached(cluster);
    }

    /// <summary>同じ更新を途中で新Coordinatorへ再発行しません。</summary>
    /// <returns>比較更新の実行結果。</returns>
    private async ValueTask<StateWriteResult> WriteAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
    {
        PeerInfo? coordinator = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status));
        if (coordinator is null)
        {
            return new StateWriteResult(false, null, EOutcome.NotExecuted, "State.CoordinatorUnavailable");
        }
        StateUpdate update = new(scope, key, expectedVersion, value);
        ClusterRequest request = ClusterRequest.StartDurable("state.cas", coordinator.PeerId, coordinator.InstanceId, TimeSpan.FromSeconds(30), NodeValue.FromObject(update));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Value?.Json.Deserialize<StateWriteResult>() ?? new StateWriteResult(false, null, response.Outcome, response.Reason);
    }

    /// <summary>Coordinatorで比較を含む複製を一操作として実施します。</summary>
    /// <returns>状態更新結果。</returns>
    private async Task<ClusterResponse> ApplyAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        StateUpdate update = request.Payload.Json.Deserialize<StateUpdate>() ?? throw new InvalidDataException("比較更新が不正です。");
        return await CompareAsync(request.Id, update, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>状態の比較更新と期限付きキーの確保で、同じ版検査と保存排他を使用します。</summary>
    /// <returns>確定した版、または比較不成立の結果です。</returns>
    private async Task<ClusterResponse> CompareAsync(Guid id, StateUpdate update, CancellationToken cancellationToken = default)
    {
        await updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCurrentAsync(id, update, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            updates.Release();
        }
    }

    /// <summary>待機中に進んだ版を再確認し、不一致の配送・prepare・abortを省きます。</summary>
    /// <returns>永続受付に対応する比較結果です。</returns>
    private async Task<ClusterResponse> ApplyCurrentAsync(Guid id, StateUpdate update, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "State.CoordinatorChanged");
        }
        if (!NodeStateScope.IsShared(update.Scope))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "State.SharedScopeRequired");
        }
        DurableEntry? old = await repository.ReadAsync(update.Scope, update.Key, cancellationToken).ConfigureAwait(false);
        string? expected = update.ExpectedVersion;
        if (expected is null && old?.Value is null)
        {
            expected = old?.Version;
        }
        if (!string.Equals(old?.Version, expected, StringComparison.Ordinal))
        {
            VersionedState? previous = null;
            if (old?.Value is not null)
            {
                previous = new(old.Version, old.Value.Copy());
            }

            // 既に不一致と分かるCASを全Peerへprepare/abortして、後続の保存を待たせません。
            string conflict = "Persistence.VersionConflict";
            return new(EOutcome.Failed, NodeValue.FromObject(new StateWriteResult(false, previous, EOutcome.Failed, conflict)), conflict);
        }
        DurableReceipt receipt = await cluster.ReplicateAsync(id, [new DurableMutation(update.Scope, update.Key, expected, update.Value)], cancellationToken).ConfigureAwait(false);
        DurableEntry? current = await repository.ReadAsync(update.Scope, update.Key, cancellationToken).ConfigureAwait(false);
        VersionedState? state = null;
        if (current?.Value is not null)
        {
            state = new VersionedState(current.Version, current.Value.Copy());
        }
        StateWriteResult result = new(receipt.Outcome == EOutcome.Succeeded, state, receipt.Outcome, receipt.Reason);
        return new ClusterResponse(receipt.Outcome, NodeValue.FromObject(result), receipt.Reason);
    }

    /// <summary>Coreの受付終了後に比較処理の待機資源を解放します。</summary>
    public void Dispose() => updates.Dispose();
}
