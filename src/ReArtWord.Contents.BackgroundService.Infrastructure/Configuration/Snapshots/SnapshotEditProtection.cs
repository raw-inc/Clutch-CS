using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>保存と復元が同じCAS行を確認する選択範囲の保護です。</summary>
internal sealed class SnapshotEditProtection(IDurableRepository repository, PeerCluster cluster)
{
    /// <summary>他Peerも確認できる保護集合を取得します。</summary>
    /// <returns>現在の保護範囲です。</returns>
    public Task<IReadOnlyList<SnapshotEditLock>> ReadAsync(CancellationToken cancellationToken = default) => ReadCoreAsync(cancellationToken);

    /// <summary>共有済みの保護行を読み取ります。</summary>
    /// <returns>現在の保護範囲です。</returns>
    private async Task<IReadOnlyList<SnapshotEditLock>> ReadCoreAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("snapshot.edit-locks", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DurableEntry> configurations = await repository.ListAsync("snapshot.configuration-locks", cancellationToken).ConfigureAwait(false);
        return entries.Concat(configurations).SelectMany(entry => entry.Value?.Json.Deserialize<SnapshotEditLock[]>(ContractJson.Options) ?? []).ToArray();
    }

    /// <summary>独立設定participantの全体保護を同じCAS契約へ接続します。</summary>
    /// <returns>原子的に含める保護条件更新、別所有者ならnullです。</returns>
    public async Task<DurableMutation?> GuardConfigurationAsync(string id, Guid? owner = null, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("snapshot.configuration-locks", id, cancellationToken).ConfigureAwait(false);
        SnapshotEditLock[] locks = entry?.Value?.Json.Deserialize<SnapshotEditLock[]>(ContractJson.Options) ?? [];
        if (locks.Any(item => item.OperationId != owner) || (owner is not null && !locks.Any(item => item.OperationId == owner)))
        {
            return null;
        }
        return new DurableMutation("snapshot.configuration-locks", id, entry?.Version, entry?.Value ?? NodeValue.FromObject(Array.Empty<SnapshotEditLock>()));
    }

    /// <summary>保存内容が保護範囲を変更しない場合だけ同じCASへ参加します。</summary>
    /// <returns>競合検査用の更新、または保護によるnullです。</returns>
    public async Task<DurableMutation?> GuardAsync(string id, WorkflowDefinition? current, WorkflowDefinition? proposed, Guid? owner = null, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("snapshot.edit-locks", id, cancellationToken).ConfigureAwait(false);
        SnapshotEditLock[] locks = entry?.Value?.Json.Deserialize<SnapshotEditLock[]>(ContractJson.Options) ?? [];
        if (locks.Any(item => item.OperationId != owner && !string.Equals(SnapshotNodeSelection.Hash(current, item.NodeIds), SnapshotNodeSelection.Hash(proposed, item.NodeIds), StringComparison.Ordinal)))
        {
            return null;
        }
        return new DurableMutation("snapshot.edit-locks", id, entry?.Version, entry?.Value ?? NodeValue.FromObject(Array.Empty<SnapshotEditLock>()));
    }

    /// <summary>全設定範囲を一括して予約し、成功対象だけへ縮小しません。</summary>
    /// <returns>保護の共有確認です。</returns>
    public async Task<ConfigurationPhaseResult> AcquireAsync(Guid operationId, IReadOnlyList<SnapshotEditLock> desired, CancellationToken cancellationToken = default)
    {
        Guid lockId = WorkflowStore.PhaseId(operationId, "edit-lock");
        DurableReceipt? known = await repository.ReadReceiptAsync(lockId, cancellationToken).ConfigureAwait(false);
        if (known is not null)
        {
            DurableReceipt? confirmed = await cluster.ConfirmReplicationAsync(lockId, cluster.GetPeers().Where(peer => peer.Status == ReArtWord.Contents.BackgroundService.Contracts.Cluster.EPeerStatus.Ready).ToArray(), cancellationToken).ConfigureAwait(false);
            return new ConfigurationPhaseResult(confirmed?.Outcome ?? EOutcome.Unknown, confirmed?.Reason);
        }
        List<DurableMutation> mutations = [];
        foreach (IGrouping<(string Id, bool Configuration), SnapshotEditLock> group in desired.GroupBy(item => (item.WorkflowId, item.IsConfigurationParticipant)))
        {
            string scope = Scope(group.Key.Configuration);
            DurableEntry? entry = await repository.ReadAsync(scope, group.Key.Id, cancellationToken).ConfigureAwait(false);
            SnapshotEditLock[] current = entry?.Value?.Json.Deserialize<SnapshotEditLock[]>(ContractJson.Options) ?? [];
            if (current.Any(item => item.OperationId != operationId))
            {
                return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.ScopeLocked");
            }
            SnapshotEditLock[] updated = current.Where(item => item.OperationId != operationId).Concat(group).ToArray();
            mutations.Add(new DurableMutation(scope, group.Key.Id, entry?.Version, NodeValue.FromObject(updated)));
        }
        if (mutations.Count == 0)
        {
            return new ConfigurationPhaseResult(EOutcome.Succeeded);
        }
        DurableReceipt result = await cluster.ReplicateAsync(lockId, mutations, cancellationToken).ConfigureAwait(false);
        return new ConfigurationPhaseResult(result.Outcome, result.Reason);
    }

    /// <summary>確認済み終了操作が保持する範囲だけを解放します。</summary>
    /// <returns>共有解除の結果です。</returns>
    public async Task<ConfigurationPhaseResult> ReleaseAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("snapshot.edit-locks", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DurableEntry> configurations = await repository.ListAsync("snapshot.configuration-locks", cancellationToken).ConfigureAwait(false);
        List<DurableMutation> mutations = [];
        foreach (DurableEntry entry in entries.Concat(configurations))
        {
            SnapshotEditLock[] current = entry.Value?.Json.Deserialize<SnapshotEditLock[]>(ContractJson.Options) ?? [];
            if (current.Any(item => item.OperationId == operationId))
            {
                mutations.Add(new DurableMutation(entry.Scope, entry.Key, entry.Version, NodeValue.FromObject(current.Where(item => item.OperationId != operationId).ToArray())));
            }
        }
        if (mutations.Count != 0)
        {
            DurableReceipt receipt = await cluster.ReplicateAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false);
            return new ConfigurationPhaseResult(receipt.Outcome, receipt.Reason);
        }
        return new ConfigurationPhaseResult(EOutcome.Succeeded);
    }

    /// <summary>ワークフロー名と独立設定名の保存キーを混同しません。</summary>
    /// <returns>保護を保存する用途です。</returns>
    private static string Scope(bool configuration)
    {
        if (configuration)
        {
            return "snapshot.configuration-locks";
        }
        return "snapshot.edit-locks";
    }

}
