using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>ワークフロー共有状態を明示離脱側だけの枝へ保存し、再参加で既存比較規則へ戻します。</summary>
internal sealed class DetachedNodeStateStore(IDurableRepository repository, PeerCluster cluster)
{
    private string Scope => "peer:" + cluster.LocalPeerId + ":detached-state";

    /// <summary>明示離脱の意図を一時的なUnavailableと区別します。</summary>
    internal bool IsDetached => cluster.IsDetached;

    /// <summary>ローカル変更がなければ最後に確認した共有値を読みます。</summary>
    /// <returns>未共有を区別した現在値です。</returns>
    internal async ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        DetachedStateEntry? local = await ReadBranchAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (local?.Pending == true)
        {
            return State(local.Current, true);
        }
        return State(await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false), false);
    }

    /// <summary>比較に成功したローカル値だけを保存し、共有rowを変更しません。</summary>
    /// <returns>ローカルCASの結果です。</returns>
    internal async ValueTask<StateWriteResult> CompareAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
    {
        string address = DurableRepository.GetAddress(scope, key);
        DurableEntry? row = await repository.ReadAsync(Scope, address, cancellationToken).ConfigureAwait(false);
        DetachedStateEntry? old = row?.Value?.Json.Deserialize<DetachedStateEntry>();
        DurableEntry? baseline = old?.Base;
        DurableEntry? current = old?.Current;
        if (old?.Pending != true)
        {
            baseline = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            current = baseline;
        }
        string? expected = expectedVersion;
        if (expected is null && current?.Value is null)
        {
            expected = current?.Version;
        }
        if (!string.Equals(expected, current?.Version, StringComparison.Ordinal))
        {
            return new(false, State(current, old?.Pending == true), EOutcome.Failed, "State.VersionConflict", true);
        }
        Guid id = Guid.NewGuid();
        DurableEntry updated = new(scope, key, id.ToString("N"), value?.Copy(), baseline?.Version);
        DurableReceipt saved = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(id, "detached.state.save"),
            [new(Scope, address, row?.Version, NodeValue.FromObject(new DetachedStateEntry(baseline, updated)))], cancellationToken).ConfigureAwait(false);
        return new(saved.Outcome == EOutcome.Succeeded, await ReadAsync(scope, key, cancellationToken).ConfigureAwait(false), saved.Outcome, saved.Reason ?? "Detached.LocalOnly", true);
    }

    /// <summary>再参加後は元版を比較し、競合は通常の候補保持へ渡します。</summary>
    /// <returns>元要求だけを追跡した統合確認です。</returns>
    internal async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (cluster.IsDetached || !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
        {
            return;
        }
        foreach (DurableEntry row in await repository.ListAsync(Scope, cancellationToken).ConfigureAwait(false))
        {
            DetachedStateEntry? branch = row.Value?.Json.Deserialize<DetachedStateEntry>();
            if (branch?.Pending != true)
            {
                continue;
            }
            DurableEntry? shared = await repository.ReadAsync(branch.Current.Scope, branch.Current.Key, cancellationToken).ConfigureAwait(false);
            if (shared is not null && (string.Equals(shared.Version, branch.Current.Version, StringComparison.Ordinal) || (shared.MergedVersions ?? []).Contains(branch.Current.Version, StringComparer.Ordinal)))
            {
                if (!string.Equals(shared.Version, branch.Current.Version, StringComparison.Ordinal) || await SharedConfirmedAsync(branch, cancellationToken).ConfigureAwait(false))
                {
                    await CompleteAsync(row, branch, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (branch.Submitted)
            {
                continue;
            }
            Guid operation = Guid.Parse(branch.Current.Version);
            DurableReceipt owned = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(operation, "detached.state.submit"),
                [new(Scope, row.Key, row.Version, NodeValue.FromObject(branch with { Submitted = true }))], cancellationToken).ConfigureAwait(false);
            if (owned.Outcome != EOutcome.Succeeded)
            {
                continue;
            }
            DurableMutation mutation = new(branch.Current.Scope, branch.Current.Key, branch.Base?.Version, branch.Current.Value);
            if (string.Equals(shared?.Version, branch.Base?.Version, StringComparison.Ordinal))
            {
                await cluster.ReplicateAsync(operation, [mutation], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                cluster.MarkSynchronizationConflict();
                DurableReceipt candidate = new(operation, ContractJson.Hash(new[] { mutation }), EOutcome.Succeeded, [branch.Current]);
                await repository.ImportAsync(new([branch.Current], [candidate]), cancellationToken).ConfigureAwait(false);
                await cluster.RefreshParticipationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>ローカルコミットを全固定対象への共有成功へ読み替えません。</summary>
    /// <returns>元操作の永続確認が揃っているか。</returns>
    private async Task<bool> SharedConfirmedAsync(DetachedStateEntry branch, CancellationToken cancellationToken = default)
    {
        Guid operation = Guid.Parse(branch.Current.Version);
        DurableReceipt? receipt = await cluster.GetOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (receipt?.Outcome == EOutcome.Unknown)
        {
            DurableEntry? members = await repository.ReadAsync("cluster:replication-members", operation.ToString("N"), cancellationToken).ConfigureAwait(false);
            PeerInfo[]? fixedMembers = members?.Value?.Json.Deserialize<PeerInfo[]>();
            if (fixedMembers is not null)
            {
                receipt = await cluster.ConfirmReplicationAsync(operation, fixedMembers, cancellationToken).ConfigureAwait(false);
            }
        }
        return receipt?.Outcome == EOutcome.Succeeded;
    }

    /// <summary>統合済み枝を再度共有へ投入せず、ローカル履歴を保持します。</summary>
    /// <returns>ローカル記録の確認です。</returns>
    private async Task CompleteAsync(DurableEntry row, DetachedStateEntry branch, CancellationToken cancellationToken = default)
    {
        await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(Scope, row.Key, row.Version, NodeValue.FromObject(branch with { Pending = false }))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>論理共有addressに対応するローカル枝を取得します。</summary>
    /// <returns>保存済みのローカル枝です。</returns>
    private async Task<DetachedStateEntry?> ReadBranchAsync(string scope, string key, CancellationToken cancellationToken = default)
        => (await repository.ReadAsync(Scope, DurableRepository.GetAddress(scope, key), cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<DetachedStateEntry>();

    /// <summary>削除と値の存在を区別して返します。</summary>
    /// <returns>存在する状態です。</returns>
    private static VersionedState? State(DurableEntry? entry, bool localOnly)
    {
        if (entry?.Value is null)
        {
            return null;
        }
        return new(entry.Version, entry.Value.Copy(), localOnly);
    }
}
