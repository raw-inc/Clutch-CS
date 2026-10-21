using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;

/// <summary>実行元が保持する到達を照合し、同じPeerへの確認をまとめます。</summary>
internal sealed class ConcurrencyOwnership
{
    private readonly PeerCluster cluster;
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<Guid, WorkflowConcurrencyRequest> owned = new();
    private static string Kind => "workflow.limit.owner";

    /// <summary>履歴やワークフローの値を含めない所有確認を登録します。</summary>
    internal ConcurrencyOwnership(PeerCluster cluster)
    {
        this.cluster = cluster;
        cluster.RegisterInspection(Kind, ReadLocalAsync);
    }

    /// <summary>元の実行だけが到達を登録し、保持数を有限にします。</summary>
    /// <returns>新たに所有できた場合はtrueです。</returns>
    internal bool TryAdd(WorkflowConcurrencyRequest request)
    {
        using (gate.EnterScope())
        {
            return string.Equals(request.OwnerPeerId, cluster.LocalPeerId, StringComparison.Ordinal) && request.OwnerInstanceId == cluster.LocalInstanceId
                && request.TicketId != Guid.Empty && owned.Count < 4096 && owned.TryAdd(request.TicketId, request);
        }
    }

    /// <summary>通知の成否にかかわらず、終了した到達の所有を閉じます。</summary>
    internal void Remove(Guid ticket) => owned.TryRemove(ticket, out _);

    /// <summary>通信不能をfalseと解釈せず、確認できた到達だけを返します。</summary>
    /// <returns>到達ごとの所有状態です。未確認の到達は含めません。</returns>
    // 抑制理由: 上限付きの所有照会を同じ回収判断に用い、途中の部分結果を公開しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW160", Justification = "最大4097件の所有照合を一括して回収判断へ返します。")]
    internal async Task<IReadOnlyDictionary<Guid, bool>> ReadAsync(IReadOnlyList<WorkflowConcurrencyRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requests.Count, 4097);
        ConcurrentDictionary<Guid, bool> results = new();
        WorkflowConcurrencyRequest[][] batches = requests.GroupBy(request => (request.OwnerPeerId, request.OwnerInstanceId))
            .SelectMany(group => group.Chunk(256)).ToArray();
        try
        {
            await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (batch, token) =>
            {
                WorkflowConcurrencyRequest owner = batch[0];
                if (owner.OwnerInstanceId == cluster.LocalInstanceId)
                {
                    foreach (WorkflowConcurrencyRequest request in batch)
                    {
                        results[request.TicketId] = owned.GetValueOrDefault(request.TicketId) == request;
                    }
                    return;
                }
                PeerInfo? peer = cluster.GetPeers().FirstOrDefault(item => string.Equals(item.PeerId, owner.OwnerPeerId, StringComparison.Ordinal));
                if (peer is null)
                {
                    return;
                }
                if (peer.InstanceId != owner.OwnerInstanceId)
                {
                    foreach (WorkflowConcurrencyRequest request in batch)
                    {
                        results[request.TicketId] = false;
                    }
                    return;
                }
                ClusterRequest query = new(Guid.NewGuid(), Kind, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(2), NodeValue.FromObject(batch));
                ClusterResponse response = await cluster.InspectAsync(query, token).ConfigureAwait(false);
                if (response.Outcome == EOutcome.Succeeded && response.Value?.Json.Deserialize<bool[]>() is { } states && states.Length == batch.Length)
                {
                    for (int index = 0; index < batch.Length; index++)
                    {
                        results[batch[index].TicketId] = states[index];
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 期限内に確認できた所有だけを返します。未確認分は回収せず、実行取消は呼出元が別に確認します。
        }
        return results;
    }

    /// <summary>呼出し元が渡した所有識別を、その実行元で一括確認します。</summary>
    /// <returns>要求順に対応する現在の所有状態です。</returns>
    private Task<ClusterResponse> ReadLocalAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowConcurrencyRequest[] requests = command.Payload.Json.Deserialize<WorkflowConcurrencyRequest[]>() ?? throw new InvalidDataException("区間の所有照会が不正です。");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requests.Length, 256);
        bool[] states = requests.Select(request => request.OwnerInstanceId == cluster.LocalInstanceId && owned.GetValueOrDefault(request.TicketId) == request).ToArray();
        return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(states)));
    }
}
