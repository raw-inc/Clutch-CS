using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>設定保存と手動取得が共用するスナップショットの永続保管です。</summary>
public sealed class SnapshotCatalog
{
    private readonly IDurableRepository repository;
    private readonly PeerCluster cluster;

    /// <summary>通常の共有保存経路を使用します。</summary>
    public SnapshotCatalog(IDurableRepository repository, PeerCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        this.repository = repository;
        this.cluster = cluster;
    }

    /// <summary>取得済み値を一スナップショットとして共有永続保存します。</summary>
    /// <returns>スナップショット段階の結果。</returns>
    public async Task<ConfigurationPhaseResult> SaveAsync(SnapshotRecord snapshot, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.Hash, ContractJson.Hash(snapshot.Value), StringComparison.Ordinal))
        {
            return new ConfigurationPhaseResult(EOutcome.Failed, "Snapshot.HashMismatch");
        }
        DurableReceipt receipt = await cluster.ReplicateAsync(operationId, [new DurableMutation("snapshots", snapshot.Id, null, NodeValue.FromObject(snapshot))], cancellationToken).ConfigureAwait(false);
        return new ConfigurationPhaseResult(receipt.Outcome, receipt.Reason);
    }

    /// <summary>保存内容を検証して読みます。</summary>
    /// <returns>存在する正常スナップショット。</returns>
    public async Task<SnapshotRecord?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("snapshots", id, cancellationToken).ConfigureAwait(false);
        entry ??= await repository.ReadAsync("peer:" + cluster.LocalPeerId + ":detached-snapshots", id, cancellationToken).ConfigureAwait(false);
        SnapshotRecord? snapshot = entry?.Value?.Json.Deserialize<SnapshotRecord>();
        if (snapshot is not null && !string.Equals(snapshot.Hash, ContractJson.Hash(snapshot.Value), StringComparison.Ordinal))
        {
            throw new InvalidDataException("snapshotの内容を確認できません。");
        }
        return snapshot;
    }

    /// <summary>一覧を非同期に取得します。</summary>
    /// <returns>保存済みスナップショット。</returns>
    public Task<IReadOnlyList<SnapshotRecord>> ListAsync(CancellationToken cancellationToken = default) => ListCoreAsync(cancellationToken);

    /// <summary>保存項目からスナップショットだけを復元します。</summary>
    /// <returns>一覧。</returns>
    private async Task<IReadOnlyList<SnapshotRecord>> ListCoreAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("snapshots", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DurableEntry> local = await repository.ListAsync("peer:" + cluster.LocalPeerId + ":detached-snapshots", cancellationToken).ConfigureAwait(false);
        return entries.Concat(local).Where(entry => entry.Value is not null).Select(entry => entry.Value!.Json.Deserialize<SnapshotRecord>() ?? throw new InvalidDataException("snapshotが不正です。")).ToArray();
    }
}
