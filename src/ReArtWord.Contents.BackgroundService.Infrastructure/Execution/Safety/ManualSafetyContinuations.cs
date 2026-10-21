using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;

/// <summary>元の続行操作だけを失効させ、新しい操作の状態は書き換えません。</summary>
internal sealed class ManualSafetyContinuations(PeerCluster cluster, IDurableRepository repository)
{
    private readonly ConcurrentDictionary<Guid, byte> expired = [];
    private readonly ConcurrentDictionary<Guid, byte> published = [];

    /// <summary>既に共有された元操作の失効だけを読みます。</summary>
    /// <returns>失効情報の取込完了です。</returns>
    public async Task ReadAsync(Guid operationId, bool detached, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || published.ContainsKey(operationId))
        {
            return;
        }
        DurableEntry? entry = await repository.ReadAsync(Scope(detached), operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (entry?.Value is not null)
        {
            expired.TryAdd(operationId, 0);
            published.TryAdd(operationId, 0);
        }
    }

    /// <summary>現在の実体と元障害の条件を照合します。</summary>
    /// <returns>実効続行と、不成立の場合の理由です。</returns>
    public (bool Active, string? Reason) Evaluate(ManualSafetySetting setting, bool? automaticallyStopped)
    {
        if (!setting.Continued)
        {
            return (false, null);
        }
        if (setting.Continuation is null || setting.OperationId == Guid.Empty)
        {
            return (false, "safety.continuationLifetimeUnknown");
        }
        if (!setting.Continuation.AutomaticStopObserved)
        {
            return (false, "safety.automaticStopNotActiveAtAcceptance");
        }
        if (expired.ContainsKey(setting.OperationId))
        {
            return (false, "safety.continuationExpired");
        }
        PeerInfo[] ready = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status) || (peer.Status == EPeerStatus.Detached && peer.InstanceId == cluster.LocalInstanceId)).ToArray();
        if (!setting.Continuation.OriginalPeers.Any(original => ready.Any(peer => string.Equals(peer.PeerId, original.PeerId, StringComparison.Ordinal) && peer.InstanceId == original.InstanceId)))
        {
            return (false, "safety.continuationSurvivorUnconfirmed");
        }
        if (automaticallyStopped is null)
        {
            return (false, "safety.automaticStateUnknown");
        }
        bool recovered = setting.Continuation.Singleton && 1 < ready.Length;
        if (!setting.Continuation.Singleton)
        {
            recovered = setting.Continuation.MissingPeerIds.Count != 0 && setting.Continuation.MissingPeerIds.All(id => ready.Any(peer => string.Equals(peer.PeerId, id, StringComparison.Ordinal)));
            if (setting.Continuation.MissingPeerIds.Count == 0)
            {
                recovered = !automaticallyStopped.Value;
            }
        }
        if (recovered)
        {
            expired.TryAdd(setting.OperationId, 0);
            return (false, "safety.continuationExpired");
        }
        return (true, null);
    }

    /// <summary>失効は元操作ID別の単調な記録として保存します。</summary>
    /// <returns>確認できた失効の保存処理完了です。</returns>
    public async Task PublishAsync(bool detached, CancellationToken cancellationToken = default)
    {
        if (detached)
        {
            return;
        }
        foreach (Guid operationId in expired.Keys.Where(id => !published.ContainsKey(id)))
        {
            string scope = Scope(detached);
            string key = operationId.ToString("N");
            DurableEntry? current = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            if (current?.Value is not null)
            {
                published.TryAdd(operationId, 0);
                continue;
            }
            DurableMutation mutation = new(scope, key, current?.Version, NodeValue.FromObject(true));
            Guid id = ManualSafetyRouter.Identity(operationId, "expire:" + scope);
            DurableReceipt result = await cluster.ReplicateAsync(id, [mutation], cancellationToken).ConfigureAwait(false);
            if (result.Outcome == EOutcome.Succeeded)
            {
                published.TryAdd(operationId, 0);
            }
        }
    }

    /// <summary>離脱中は通常クラスタの操作へ失効を書き込みません。</summary>
    /// <returns>失効情報の保存先です。</returns>
    private string Scope(bool detached)
    {
        if (detached)
        {
            return "peer:" + cluster.LocalPeerId + ":safety-expiry";
        }
        return "execution:safety-expiry";
    }
}
