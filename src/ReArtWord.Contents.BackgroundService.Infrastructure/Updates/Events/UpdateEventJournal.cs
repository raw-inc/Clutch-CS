using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Events;

/// <summary>独立Updaterが確認した状態変化を、共有状態とは別の発行待ちへ引き渡します。</summary>
internal static class UpdateEventJournal
{
    /// <summary>操作の所有Peerだけが通知するローカル保存先です。</summary>
    /// <returns>実行時用途の保存スコープです。</returns>
    internal static string Scope(string peer) => "peer:" + peer + ":update-events";

    /// <summary>開始・対象変化・終端を、同じ内容に同じ事象IDで作成します。</summary>
    /// <returns>更新状態の確認後に保存する通知です。</returns>
    internal static async Task<IReadOnlyList<DurableMutation>> CreateAsync(IDurableRepository repository, string owner,
        ClusterUpdateStatus? before, ClusterUpdateStatus current, CancellationToken cancellationToken = default)
    {
        List<(string Name, UpdateEventData Data)> changes = [];
        UpdateEventData summary = new(current.Request.OperationId, current.Outcome, current.Completed, ReleaseId: current.Request.Manifest.ReleaseId, Reason: current.Reason);
        if (before is null)
        {
            changes.Add(("update.started", new(current.Request.OperationId, EOutcome.Unknown, false, ReleaseId: current.Request.Manifest.ReleaseId)));
        }
        foreach (UpdatePeerResult peer in current.Peers)
        {
            UpdatePeerResult? previous = before?.Peers.FirstOrDefault(item => string.Equals(item.PeerId, peer.PeerId, StringComparison.Ordinal));
            if (previous == peer)
            {
                continue;
            }
            bool completed = peer.Phase is EUpdatePhase.Completed or EUpdatePhase.RolledBack or EUpdatePhase.Failed or EUpdatePhase.Interrupted;
            changes.Add(("update.peerChanged", new(peer.OperationId, peer.Outcome, completed, peer.PeerId, peer.Phase,
                peer.PreparedInstanceId, peer.InstanceId, peer.ReleaseId, peer.Reason)));
        }
        if (current.Completed && before?.Completed != true)
        {
            changes.Add(("update.completed", summary));
            if (current.Outcome == EOutcome.Failed)
            {
                changes.Add(("update.failed", summary));
            }
            if (current.Outcome == EOutcome.Unknown)
            {
                changes.Add(("update.interrupted", summary));
            }
        }
        List<DurableMutation> mutations = [];
        foreach ((string name, UpdateEventData data) in changes)
        {
            Guid eventId = WorkflowStore.PhaseId(data.OperationId, name + ":" + ContractJson.Hash(data));
            string key = eventId.ToString("N");
            if (await repository.ReadAsync(Scope(owner), key, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }
            WorkflowEvent notification = new(eventId, name, DateTimeOffset.UtcNow, owner, ContractJson.ToValue(data), CausationId: data.OperationId);
            mutations.Add(new(Scope(owner), key, null, ContractJson.ToValue(notification)));
        }
        return mutations;
    }
}
