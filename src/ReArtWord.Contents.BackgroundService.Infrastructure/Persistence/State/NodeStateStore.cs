using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>Peerローカル状態とワークフロー共有状態の保存経路を分けます。</summary>
public sealed class NodeStateStore : INodeStateStore, IObservableNodeStateStore
{
    private readonly IDurableRepository repository;
    private readonly string localPeerId;
    private DetachedNodeStateStore? detached;

    /// <summary>共有状態にも明示離脱のローカル枝を使用する境界を接続します。</summary>
    internal void BindDetached(Cluster.PeerCluster cluster) => detached = new(repository, cluster);

    /// <summary>再参加後のローカル枝を元共有版と照合します。</summary>
    /// <returns>非競合統合または競合候補保持の確認です。</returns>
    public Task ReconcileDetachedAsync(CancellationToken cancellationToken = default) => detached?.ReconcileAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>保存先とローカルPeer識別子を指定します。</summary>
    public NodeStateStore(IDurableRepository repository, string localPeerId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        this.repository = repository;
        this.localPeerId = localPeerId;
    }

    /// <summary>共有状態をCoordinatorで比較更新する配送境界です。</summary>
    public Func<string, string, string?, NodeValue?, CancellationToken, ValueTask<StateWriteResult>>? SharedWriter
    {
        get; set;
    }

    /// <summary>永続確定値だけを購読し、未共有の編集候補で後続を発火しません。</summary>
    /// <returns>初期値と値変更の通知列です。</returns>
    public IAsyncEnumerable<StateChange> WatchAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return NodeStateObservation.WatchAsync(repository, scope, key, cancellationToken);
    }

    /// <summary>ローカルまたは共有範囲の現在値を読みます。</summary>
    /// <returns>確認した保存結果です。</returns>
    public async ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (detached?.IsDetached == true && NodeStateScope.IsShared(scope))
        {
            return await detached.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        }
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (entry?.Value is null)
        {
            return null;
        }
        return new VersionedState(entry.Version, entry.Value.Copy());
    }

    /// <summary>共有更新はCoordinatorへ送り、ローカル更新は指定Peer内で比較します。</summary>
    /// <returns>確認した保存結果です。</returns>
    public async ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (detached?.IsDetached == true && NodeStateScope.IsShared(scope))
        {
            return await detached.CompareAsync(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        }
        if (NodeStateScope.IsShared(scope))
        {
            if (SharedWriter is null)
            {
                return new StateWriteResult(false, null, EOutcome.NotExecuted, "State.SharedStoreUnavailable");
            }
            return await SharedWriter(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        }
        DurableEntry? old = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        string? expected = expectedVersion;
        if (expected is null && old?.Value is null)
        {
            expected = old?.Version;
        }
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, key, expected, value)], cancellationToken).ConfigureAwait(false);
        VersionedState? current = await ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        return new StateWriteResult(receipt.Outcome == EOutcome.Succeeded, current, receipt.Outcome, receipt.Reason);
    }

    /// <summary>別Peerのローカル保存へ到達しないことを確認します。</summary>
    private void ValidateScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (!NodeStateScope.IsNodeScope(scope, localPeerId))
        {
            throw new ArgumentException("保存範囲はクラスタ共有、Workflow共有、またはこのPeerの局所範囲に限ります。", nameof(scope));
        }
    }
}
