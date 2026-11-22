using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>実行ピアの日時を使わず、現在の共有状態と同じ版に対してだけ有限TTLのキーを確保します。</summary>
internal sealed class SharedDedupeReservations(PeerCluster cluster, IDurableRepository repository, NodeStateStore store,
    Func<Guid, StateUpdate, CancellationToken, Task<ClusterResponse>> compare)
{
    private readonly DedupeLeaseClock leases = new(cluster, repository);

    internal static string Kind => "state.dedupe.reserve";

    /// <summary>所属する側の担当へ一度だけ送り、明示離脱時はローカル限定の結果を返します。</summary>
    /// <returns>元の操作の成否です。担当が変わっても別の要求を送りません。</returns>
    internal async ValueTask<StateWriteResult> ReserveAsync(string scope, string key, TimeSpan? lifetime, CancellationToken cancellationToken = default)
    {
        Reservation reservation = new(scope, key, lifetime?.Ticks);
        if (cluster.IsDetached)
        {
            return await ApplyAsync(OperationIdentity.Create(), reservation, true, cancellationToken).ConfigureAwait(false);
        }
        PeerInfo? owner = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status));
        if (owner is null)
        {
            return new(false, null, EOutcome.NotExecuted, "State.CoordinatorUnavailable");
        }
        ClusterRequest request = ClusterRequest.StartDurable(Kind, owner.PeerId, owner.InstanceId, TimeSpan.FromSeconds(30), NodeValue.FromObject(reservation));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Value?.Json.Deserialize<StateWriteResult>(ContractJson.Options) ?? new(false, null, response.Outcome, response.Reason);
    }

    /// <summary>固定された担当実体で受け付け、比較保存に元の要求IDを使います。</summary>
    /// <returns>永続受付へ保存する結果です。</returns>
    internal async Task<ClusterResponse> HandleAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        Reservation body = request.Payload.Json.Deserialize<Reservation>(ContractJson.Options) ?? throw new InvalidDataException();
        StateWriteResult result = await ApplyAsync(request.Id, body, false, cancellationToken).ConfigureAwait(false);
        return new(result.Outcome, NodeValue.FromObject(result), result.Reason);
    }

    /// <summary>期限を確認した後に版が変われば上書きせず、分断中に分かれた候補も保持します。</summary>
    /// <returns>確認済みの既存キー、今回の確保、または確認不能です。</returns>
    private async ValueTask<StateWriteResult> ApplyAsync(Guid id, Reservation body, bool detached, CancellationToken cancellationToken = default)
    {
        if (!NodeStateScope.IsShared(body.Scope) || string.IsNullOrWhiteSpace(body.Key) || body.LifetimeTicks is <= 0 || TimeSpan.FromMilliseconds(int.MaxValue - 1).Ticks < body.LifetimeTicks)
        {
            return new(false, null, EOutcome.NotExecuted, "State.InvalidReservation");
        }
        if (!Owns(detached))
        {
            return new(false, null, EOutcome.NotExecuted, "State.CoordinatorChanged");
        }
        (DurableEntry? entry, IReadOnlyList<DurableEntry> conflicts) = await repository.ReadWithConflictsAsync(body.Scope, body.Key, cancellationToken).ConfigureAwait(false);
        if (conflicts.Count != 0)
        {
            return new(false, null, EOutcome.Unknown, "State.ReservationConflict", detached);
        }
        VersionedState? previous = null;
        if (detached)
        {
            previous = await store.Detached!.ReadAsync(body.Scope, body.Key, cancellationToken).ConfigureAwait(false);
        }
        else if (entry?.Value is not null)
        {
            previous = new(entry.Version, entry.Value);
        }
        if (previous is not null)
        {
            bool? expired = await leases.ExpiredAsync(body.Scope, body.Key, previous, detached, cancellationToken).ConfigureAwait(false);
            if (!Owns(detached))
            {
                return new(false, null, EOutcome.Unknown, "State.CoordinatorChanged");
            }
            if (expired != true)
            {
                if (expired is null)
                {
                    return new(false, previous, EOutcome.Unknown, "State.ReservationClockUnconfirmed", detached);
                }
                return new(false, previous, EOutcome.Succeeded, LocalOnly: detached);
            }
        }
        NodeValue marker = leases.Create(id, body.LifetimeTicks);
        if (!Owns(detached))
        {
            return new(false, previous, EOutcome.NotExecuted, "State.CoordinatorChanged");
        }
        if (detached)
        {
            return await store.Detached!.CompareAsync(body.Scope, body.Key, previous?.Version, marker, cancellationToken).ConfigureAwait(false);
        }
        ClusterResponse saved = await compare(id, new(body.Scope, body.Key, entry?.Version, marker), cancellationToken).ConfigureAwait(false);
        StateWriteResult? committed = saved.Value?.Json.Deserialize<StateWriteResult>(ContractJson.Options);
        if (committed?.Applied == true)
        {
            return committed;
        }

        // 同じキーの同時確保で版が進んだ場合は、新版の有効性を確認してから重複と判定します。
        string conflict = "Persistence.VersionConflict";
        if (string.Equals(saved.Reason, conflict, StringComparison.Ordinal))
        {
            (DurableEntry? winner, IReadOnlyList<DurableEntry> candidates) = await repository.ReadWithConflictsAsync(body.Scope, body.Key, cancellationToken).ConfigureAwait(false);
            if (winner?.Value is not null && candidates.Count == 0)
            {
                VersionedState current = new(winner.Version, winner.Value);
                if (await leases.ExpiredAsync(body.Scope, body.Key, current, false, cancellationToken).ConfigureAwait(false) == false)
                {
                    return new(false, current, EOutcome.Succeeded);
                }
            }
        }
        return committed ?? new(false, null, saved.Outcome, saved.Reason);
    }

    /// <summary>判定中に担当や明示離脱状態が変われば、元の側での確定には使えません。</summary>
    /// <returns>この実体が引き続き選択した側の状態を所有しているかです。</returns>
    private bool Owns(bool detached) => detached == cluster.IsDetached && (detached || string.Equals(cluster.LocalPeerId, cluster.CoordinatorId, StringComparison.Ordinal));

    /// <summary>所有側へ固定して送る元要求です。</summary>
    private sealed record Reservation(string Scope, string Key, long? LifetimeTicks);
}
