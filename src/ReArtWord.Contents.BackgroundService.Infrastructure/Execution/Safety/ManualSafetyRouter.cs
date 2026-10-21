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

/// <summary>承認要求の元Coordinator実体と期限を再送を通じて維持します。</summary>
internal sealed class ManualSafetyRouter(PeerCluster cluster, IDurableRepository repository) : IDisposable
{
    private readonly SemaphoreSlim routing = new(1, 1);

    /// <summary>未開始だけを現担当へ固定し、既存要求を新実体へ差し替えません。</summary>
    /// <returns>元操作記録で確認した受付結果です。</returns>
    public async Task<DurableReceipt> SendAsync(ManualSafetyChange change, CancellationToken cancellationToken = default)
    {
        await routing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendCoreAsync(change, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            routing.Release();
        }
    }

    /// <summary>同時再送でも一つの元要求だけを保存して使用します。</summary>
    /// <returns>配送結果です。</returns>
    private async Task<DurableReceipt> SendCoreAsync(ManualSafetyChange change, CancellationToken cancellationToken = default)
    {
        string scope = "peer:" + cluster.LocalPeerId + ":safety-requests";
        string key = change.OperationId.ToString("N");
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        ClusterRequest? command = entry?.Value?.Json.Deserialize<ClusterRequest>();
        if (command is not null)
        {
            ManualSafetyChange? original = command.Payload.Json.Deserialize<ManualSafetyChange>();
            if (!string.Equals(ContractJson.Hash(original), ContractJson.Hash(change), StringComparison.Ordinal))
            {
                return Result(change, EOutcome.Failed, "safety.operationMismatch");
            }
            return await QueryCommandAsync(command, change, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            PeerInfo[] peers = cluster.GetPeers().ToArray();
            bool detached = cluster.IsDetached;
            string? target = cluster.CoordinatorId;
            if (detached)
            {
                target = cluster.LocalPeerId;
            }
            PeerInfo? owner = peers.FirstOrDefault(peer => string.Equals(peer.PeerId, target, StringComparison.Ordinal) && (PeerStatusRules.CanParticipate(peer.Status) || (detached && peer.InstanceId == cluster.LocalInstanceId)));
            if (owner is null)
            {
                return Result(change, EOutcome.NotExecuted, "safety.coordinatorUnavailable");
            }
            command = new ClusterRequest(change.OperationId, "safety.change", owner.PeerId, owner.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5), NodeValue.FromObject(change));
            DurableReceipt saved = await repository.ApplyBatchAsync(Identity(change.OperationId, "route"), [new DurableMutation(scope, key, null, NodeValue.FromObject(command))], cancellationToken).ConfigureAwait(false);
            if (saved.Outcome != EOutcome.Succeeded)
            {
                return Result(change, EOutcome.Unknown, "safety.routeUnconfirmed");
            }
        }
        ClusterResponse response = await cluster.RequestAsync(command, cancellationToken).ConfigureAwait(false);
        return response.Value?.Json.Deserialize<DurableReceipt>() ?? Result(change, response.Outcome, response.Reason);
    }

    /// <summary>配送済みの元要求だけを読み取り専用で照会します。</summary>
    /// <returns>元受付結果、またはローカル未記録のnullです。</returns>
    public async Task<DurableReceipt?> QueryAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("peer:" + cluster.LocalPeerId + ":safety-requests", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        ClusterRequest? command = entry?.Value?.Json.Deserialize<ClusterRequest>();
        if (command is null)
        {
            return null;
        }
        ManualSafetyChange change = command.Payload.Json.Deserialize<ManualSafetyChange>()!;
        return await QueryCommandAsync(command, change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>操作記録がなければ副作用本体を再送しません。</summary>
    /// <returns>保存済み結果または進行中・結果不明です。</returns>
    private async Task<DurableReceipt> QueryCommandAsync(ClusterRequest command, ManualSafetyChange change, CancellationToken cancellationToken = default)
    {
        ClusterRequestStatus status = await cluster.QueryRequestAsync(command, cancellationToken).ConfigureAwait(false);
        if (status.Response is not null)
        {
            return status.Response.Value?.Json.Deserialize<DurableReceipt>() ?? Result(change, status.Response.Outcome, status.Response.Reason);
        }
        return Result(change, EOutcome.Unknown, status.Reason ?? "safety.originalRequestRunning");
    }

    /// <summary>元操作と用途だけから一定のローカル処理IDを作ります。</summary>
    /// <returns>用途別の一定IDです。</returns>
    internal static Guid Identity(Guid operationId, string purpose) => new(Convert.FromHexString(ContractJson.Hash(new { operationId, purpose })).AsSpan(0, 16));

    /// <summary>取得不能を実施済みへ丸めません。</summary>
    /// <returns>元要求を識別する結果です。</returns>
    internal static DurableReceipt Result(ManualSafetyChange request, EOutcome outcome, string? reason) => new(request.OperationId, ContractJson.Hash(request), outcome, [], reason);

    /// <summary>ローカル受付の排他を解放します。</summary>
    public void Dispose() => routing.Dispose();
}
