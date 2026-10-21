using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>共通運用設定を既存Configuration スナップショットの段階と比較へ接続します。</summary>
public sealed class ClusterConfigurationSnapshotParticipant : IConfigurationSnapshotParticipant
{
    private readonly ClusterConfigurationStore store;
    private readonly SnapshotCatalog snapshots;

    /// <summary>正常な内容検査値を持つ保存時または手動スナップショットを確認します。</summary>
    /// <returns>指定保存版のスナップショットが確認できたかです。</returns>
    internal async Task<bool> HasSavedRevisionAsync(string revision, CancellationToken cancellationToken = default)
    {
        foreach (SnapshotRecord item in await snapshots.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind != ESnapshotKind.Configuration || !string.Equals(item.TargetId, ClusterConfigurationStore.Id, StringComparison.Ordinal) || !string.Equals(item.Hash, ContractJson.Hash(item.Value), StringComparison.Ordinal))
            {
                continue;
            }
            ClusterOperationalState? state = item.Value.Json.Deserialize<ClusterOperationalState>();
            if (state?.Saved is ClusterOperationalSnapshot saved && string.Equals(saved.Revision, revision, StringComparison.Ordinal) && string.Equals(saved.Revision, ContractJson.Hash(saved.Settings), StringComparison.Ordinal) && ClusterConfigurationValidation.Validate(saved.Settings).Count == 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>保存と実有効版を管理する一つのstoreを使います。</summary>
    public ClusterConfigurationSnapshotParticipant(ClusterConfigurationStore store, SnapshotCatalog snapshots)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        ArgumentNullException.ThrowIfNull(snapshots);
        this.snapshots = snapshots;
    }

    /// <summary>ワークフローから分離した設定対象です。</summary>
    public string Id => ClusterConfigurationStore.Id;

    /// <summary>保存と実有効版を同じ排他で取得します。</summary>
    /// <returns>実際の設定状態です。</returns>
    public Task<NodeValue> CaptureAsync(CancellationToken cancellationToken = default) => store.CaptureStateAsync(cancellationToken);

    /// <summary>現在値を変えず復元候補の両版を検証します。</summary>
    /// <returns>復元前の問題です。</returns>
    public Task<IReadOnlyList<ValidationIssue>> ValidateAsync(NodeValue snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        List<ValidationIssue> issues = [];
        try
        {
            ClusterOperationalState state = snapshot.Json.Deserialize<ClusterOperationalState>() ?? throw new JsonException();
            if (!Enum.IsDefined(state.SavedActivationPolicy))
            {
                issues.Add(new("cluster.snapshot.activationPolicy", "保存版の適用方針が不正です。"));
            }
            foreach (ClusterOperationalSnapshot? item in new[] { state.Saved, state.Effective })
            {
                if (item is not null)
                {
                    issues.AddRange(ClusterConfigurationValidation.Validate(item.Settings));
                    if (!string.Equals(item.Revision, ContractJson.Hash(item.Settings), StringComparison.Ordinal))
                    {
                        issues.Add(new("cluster.snapshot.revisionMismatch", "snapshotの版と内容が一致しません。"));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            issues.Add(new("cluster.snapshot.invalid", "共通設定snapshotの構造が不正です。"));
        }
        return Task.FromResult<IReadOnlyList<ValidationIssue>>(issues);
    }

    /// <summary>外側のスナップショット操作が固定した一回の復元を行います。</summary>
    /// <returns>共有と実復元の結果です。</returns>
    public Task<ConfigurationPhaseResult> RestoreAsync(NodeValue snapshot, CancellationToken cancellationToken = default) => store.RestoreSnapshotAsync(snapshot, cancellationToken);

    /// <summary>設定保護の所有者を外側スナップショット操作から引き継ぎます。</summary>
    /// <returns>共有と実復元の結果です。</returns>
    public Task<ConfigurationPhaseResult> RestoreAsync(Guid restoreOperationId, NodeValue snapshot, CancellationToken cancellationToken = default) => store.RestoreOwnedSnapshotAsync(restoreOperationId, snapshot, cancellationToken);

    /// <summary>復元する保存版と反映希望を同じ既存原子保存へ渡します。</summary>
    /// <returns>固定内容の共有復元結果です。</returns>
    internal static async Task<ConfigurationPhaseResult> RestoreSavedAsync(ClusterOperationalState state, IDurableRepository repository, PeerCluster cluster, DurableMutation guard, CancellationToken cancellationToken = default)
    {
            DurableEntry? saved = await repository.ReadAsync(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
            DurableEntry? marker = await repository.ReadAsync(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
            NodeValue? savedValue = null;
            NodeValue? markerValue = null;
            if (state.Saved is not null)
            {
                savedValue = NodeValue.FromObject(new ClusterConfigurationStored(state.Saved, state.SavedActivationPolicy));
            }
            if (state.Effective is not null)
            {
                markerValue = NodeValue.FromObject(state.Effective);
            }
            List<DurableMutation> mutations = [new(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, saved?.Version, savedValue), new(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, marker?.Version, markerValue), guard];
            if (state.Saved is not null && await repository.ReadAsync(ClusterConfigurationStore.HistoryScope, state.Saved.Revision, cancellationToken).ConfigureAwait(false) is null)
            {
                mutations.Add(new(ClusterConfigurationStore.HistoryScope, state.Saved.Revision, null, savedValue));
            }
            DurableReceipt restored = await cluster.ReplicateAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false);
        return new(restored.Outcome, restored.Reason);
    }

    /// <summary>保存版とローカル実有効版を実値で比較します。</summary>
    /// <returns>宣言した状態との一致です。</returns>
    public async Task<bool> MatchesAsync(NodeValue snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((await ValidateAsync(snapshot, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return false;
        }
        return string.Equals(ContractJson.Hash(snapshot), ContractJson.Hash(await CaptureAsync(cancellationToken).ConfigureAwait(false)), StringComparison.Ordinal);
    }
}
