using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;

/// <summary>現Coordinatorの受付を直列化し、編集CASの三択を稼働操作へ持ち込みません。</summary>
internal sealed class ManualSafetyAdmission(PeerCluster cluster, IDurableRepository repository, Func<string, string, bool?> automaticStop) : IDisposable
{
    private readonly SemaphoreSlim changes = new(1, 1);
    private long sequence;

    /// <summary>受付順に直前状態へ適用します。</summary>
    /// <returns>今回または保存済み受付の結果です。</returns>
    public async Task<ClusterResponse> AcceptAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        ManualSafetyChange request = command.Payload.Json.Deserialize<ManualSafetyChange>() ?? throw new InvalidDataException("安全操作要求が不正です。");
        if (request.OperationId != command.Id || request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkflowId) || string.IsNullOrWhiteSpace(request.Scope) || (request.Stopped && request.Continued))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "safety.invalidManualMode");
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableReceipt receipt = await AcceptCoreAsync(request, cancellationToken).ConfigureAwait(false);
            return new ClusterResponse(receipt.Outcome, NodeValue.FromObject(receipt), receipt.Reason);
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>新操作だけを直前の確定行へ適用し、同IDの再実施を防ぎます。</summary>
    /// <returns>共有または離脱時ローカル保存の結果です。</returns>
    private async Task<DurableReceipt> AcceptCoreAsync(ManualSafetyChange request, CancellationToken cancellationToken = default)
    {
        bool detached = cluster.IsDetached;
        string scope = Scope(cluster.LocalPeerId, detached);
        string operationScope = scope + ":operations";
        DurableEntry? operation = await repository.ReadAsync(operationScope, request.OperationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (operation?.Value is not null)
        {
            if (!string.Equals(operation.Value.GetString(), ContractJson.Hash(request), StringComparison.Ordinal))
            {
                return ManualSafetyRouter.Result(request, EOutcome.Failed, "safety.operationMismatch");
            }
            DurableReceipt? known = await cluster.GetOperationAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (detached)
            {
                known = await repository.ReadReceiptAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            }
            return known ?? ManualSafetyRouter.Result(request, EOutcome.Unknown, "safety.originalResultUnknown");
        }
        if (!detached && !string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return ManualSafetyRouter.Result(request, EOutcome.NotExecuted, "safety.coordinatorChanged");
        }
        ManualSafetyEntry[] observed = ManualSafetyState.Read(await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false), detached);
        Guid[] supersedes = ManualSafetyState.Frontier(observed).Where(entry => string.Equals(entry.Setting.WorkflowId, request.WorkflowId, StringComparison.Ordinal) && string.Equals(entry.Setting.Scope, request.Scope, StringComparison.Ordinal)).Select(entry => entry.Setting.OperationId).ToArray();
        ManualSafetyContinuation? continuation = null;
        if (request.Continued)
        {
            bool? automaticallyStopped = automaticStop(request.WorkflowId, request.Scope);
            if (automaticallyStopped is null)
            {
                return ManualSafetyRouter.Result(request, EOutcome.NotExecuted, "safety.automaticStopNotConfirmed");
            }
            PeerInfo[] peers = cluster.GetPeers().Where(peer => (detached && peer.InstanceId == cluster.LocalInstanceId) || (!detached && peer.Status != EPeerStatus.Detached)).ToArray();
            ManualSafetyPeer[] ready = peers.Where(peer => detached || PeerStatusRules.CanParticipate(peer.Status)).Select(peer => new ManualSafetyPeer(peer.PeerId, peer.InstanceId)).ToArray();
            if (ready.Length == 0)
            {
                return ManualSafetyRouter.Result(request, EOutcome.NotExecuted, "safety.continuationSurvivorUnconfirmed");
            }
            continuation = new(ready, ready.Length <= 1, peers.Where(peer => !detached && !PeerStatusRules.CanParticipate(peer.Status)).Select(peer => peer.PeerId).Order(StringComparer.Ordinal).ToArray(), automaticallyStopped.Value);
        }
        ManualSafetyAcceptance acceptance = new(cluster.LocalPeerId, cluster.LocalInstanceId, Interlocked.Increment(ref sequence));
        ManualSafetySetting setting = new(request.WorkflowId, request.Scope, request.Stopped, request.Continued, request.OperationId, acceptance, continuation, supersedes);
        DurableMutation[] mutations = [new(scope, request.OperationId.ToString("N"), null, NodeValue.FromObject(setting)), new(operationScope, request.OperationId.ToString("N"), null, NodeValue.FromObject(ContractJson.Hash(request)))];
        if (detached)
        {
            return await repository.ApplyBatchAsync(request.OperationId, mutations, cancellationToken).ConfigureAwait(false);
        }
        return await cluster.ReplicateAsync(request.OperationId, mutations, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>離脱時の人為状態を通常クラスタへ混ぜません。</summary>
    /// <returns>現在使う保存用途です。</returns>
    internal static string Scope(string peerId, bool detached)
    {
        if (detached)
        {
            return "peer:" + peerId + ":safety";
        }
        return "execution:safety";
    }

    /// <summary>受付の排他を解放します。</summary>
    public void Dispose() => changes.Dispose();
}
