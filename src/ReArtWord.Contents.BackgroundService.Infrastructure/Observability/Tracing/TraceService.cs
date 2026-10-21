using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Tracing;

/// <summary>保存通常方針から独立した、明示解除までの一時追跡を共有します。</summary>
public sealed class TraceService : ITracePolicyProvider, IDisposable
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly SemaphoreSlim changes = new(1, 1);
    private TraceView current = new(null, new Dictionary<string, TraceSelection>(StringComparer.Ordinal), false, null);
    private static string Scope => "trace:temporary";
    private static string Key => "current";

    /// <summary>通常共有保存と同じ固定参加集合の確認を使用します。</summary>
    public TraceService(PeerCluster cluster, IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.cluster = cluster;
        this.repository = repository;
    }

    /// <summary>起動時に保存状態を取込済みか。</summary>
    public bool Loaded => Volatile.Read(ref current).Loaded;

    /// <summary>ワークフロー一覧が空でも比較操作に使える一時方針表の版です。</summary>
    public string? Version => Volatile.Read(ref current).Version;

    /// <summary>一時操作中のワークフロー一覧です。</summary>
    public IReadOnlyList<string> WorkflowIds => Volatile.Read(ref current).Policies.Keys.ToArray();

    /// <summary>開始する一件へ固定する方針を取得します。</summary>
    /// <returns>保存方針、または一時的な詳細追跡です。</returns>
    public TracePolicy GetPolicy(string workflowId, TracePolicy saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (Volatile.Read(ref current).Policies.TryGetValue(workflowId, out TraceSelection? selection))
        {
            return new TracePolicy(true, selection);
        }
        return saved;
    }

    /// <summary>通常方針・一時操作・現在のローカル確認を分けて返します。</summary>
    /// <returns>表示用の実効状態です。</returns>
    public TraceStatus GetStatus(string workflowId, TracePolicy saved, TracePolicy? activeNormal = null)
    {
        TraceView state = Volatile.Read(ref current);
        return new(workflowId, saved, GetPolicy(workflowId, activeNormal ?? saved), state.Policies.ContainsKey(workflowId), state.Version, state.Loaded, state.Reason, activeNormal ?? saved);
    }

    /// <summary>操作期限や資源圧迫で一時方針を自動解除せず、保存値を取り込みます。</summary>
    /// <returns>読取の完了です。</returns>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if ((await repository.GetConflictsAsync(Scope, cancellationToken).ConfigureAwait(false)).Count != 0)
            {
                Volatile.Write(ref current, Volatile.Read(ref current) with { Loaded = false, Reason = "Trace.StateConflict" });
                return;
            }
            DurableEntry? entry = await repository.ReadAsync(Scope, Key, cancellationToken).ConfigureAwait(false);
            Dictionary<string, TraceSelection> policies = entry?.Value?.Json.Deserialize<Dictionary<string, TraceSelection>>(ContractJson.Options) ?? new(StringComparer.Ordinal);
            Volatile.Write(ref current, new TraceView(entry?.Version, policies, true, null));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Volatile.Write(ref current, Volatile.Read(ref current) with { Loaded = false, Reason = "Trace.StateUnavailable" });
        }
    }

    /// <summary>一時追跡を開始し、解除時は保存通常方針へ戻します。</summary>
    /// <returns>共有保存の確認結果です。</returns>
    public async Task<DurableReceipt> ChangeAsync(TraceChange request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        if (request.Selection?.RawByteLimit is int limit && limit <= 0)
        {
            return Rejected(request.OperationId, "Trace.RawByteLimitInvalid");
        }
        return await ApplyAsync(request.OperationId, request.ExpectedVersion, request, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>人が選んだ全停止操作の先行段階として、一時追跡だけを解除します。</summary>
    /// <returns>元参加集合すべての保存確認です。</returns>
    public Task<DurableReceipt> ClearAsync(Guid operationId, string? expectedVersion, IReadOnlyList<PeerInfo> members, Guid? stopOperationId = null, CancellationToken cancellationToken = default)
        => ApplyAsync(operationId, expectedVersion, null, members, stopOperationId, cancellationToken);

    /// <summary>全停止完了とは推定せず、追跡再開始を保留する操作を照会します。</summary>
    /// <returns>現在の停止保留と版です。</returns>
    public async ValueTask<TraceStopState> GetStopStateAsync(CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, "closing", cancellationToken).ConfigureAwait(false);
        Guid? id = entry?.Value?.Json.Deserialize<Guid>();
        return new(id, entry?.Version);
    }

    /// <summary>人の確認により追跡再開始を許可します。受理済み停止は取消しません。</summary>
    /// <returns>保留解除の共有確認です。</returns>
    public ValueTask<DurableReceipt> ReleaseStopAsync(TraceStopReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return cluster.ReplicateAsync(request.OperationId, [new DurableMutation(Scope, "closing", request.ExpectedVersion, null)], cancellationToken);
    }

    /// <summary>同じ操作の確定結果を副作用なしで照会します。</summary>
    /// <returns>共有操作結果または未受理のnullです。</returns>
    public ValueTask<DurableReceipt?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) => cluster.GetOperationAsync(operationId, cancellationToken);

    /// <summary>同じ操作IDへ最初の比較更新内容を固定します。</summary>
    /// <returns>元の保存内容による共有結果です。</returns>
    private async Task<DurableReceipt> ApplyAsync(Guid operationId, string? expectedVersion, TraceChange? change, IReadOnlyList<PeerInfo>? members, Guid? stopOperationId, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string journalScope = "peer:" + cluster.LocalPeerId + ":trace-operations";
            string fingerprint = ContractJson.Hash(new { operationId, expectedVersion, change, members, stopOperationId });
            DurableEntry? journal = await repository.ReadAsync(journalScope, operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
            TraceOperation? operation = journal?.Value?.Json.Deserialize<TraceOperation>(ContractJson.Options);
            if (operation is not null && !string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Rejected(operationId, "Trace.OperationMismatch");
            }
            if (operation is null)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                TraceView state = Volatile.Read(ref current);
                TraceStopState closing = await GetStopStateAsync(cancellationToken).ConfigureAwait(false);
                if (change?.Enabled == true && closing.OperationId is not null)
                {
                    return Rejected(operationId, "Trace.ClusterStopPending");
                }
                if (!state.Loaded || !string.Equals(state.Version, expectedVersion, StringComparison.Ordinal))
                {
                    return Rejected(operationId, "Trace.ExpectedVersionMismatchOrUnavailable");
                }
                Dictionary<string, TraceSelection> desired = new(state.Policies, StringComparer.Ordinal);
                if (change is null)
                {
                    desired.Clear();
                }
                else
                {
                    if (change.Enabled)
                    {
                        desired[change.WorkflowId] = change.Selection ?? new TraceSelection();
                    }
                    else
                    {
                        desired.Remove(change.WorkflowId);
                    }
                }
                List<DurableMutation> mutations = [new DurableMutation(Scope, Key, expectedVersion, NodeValue.FromObject(desired))];
                if (stopOperationId is Guid stopId)
                {
                    mutations.Add(new DurableMutation(Scope, "closing", closing.Version, NodeValue.FromObject(stopId)));
                }
                operation = new(fingerprint, mutations);
                DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(journalScope, operationId.ToString("N"), null, NodeValue.FromObject(operation))], cancellationToken).ConfigureAwait(false);
                if (saved.Outcome != EOutcome.Succeeded)
                {
                    return Rejected(operationId, "Trace.OperationNotRecorded");
                }
            }
            DurableReceipt result;
            if (members is null)
            {
                result = await cluster.ReplicateAsync(operationId, operation.Mutations, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await cluster.ReplicateFixedAsync(operationId, operation.Mutations, members, cancellationToken).ConfigureAwait(false);
            }
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>未実施の操作理由を返します。</summary>
    /// <returns>変更を行っていない結果です。</returns>
    private static DurableReceipt Rejected(Guid id, string reason) => new(id, string.Empty, EOutcome.NotExecuted, [], reason);

    /// <summary>操作直列化資源を解放します。</summary>
    public void Dispose() => changes.Dispose();

    /// <summary>読取へ一度に公開する不変参照です。</summary>
    private sealed record TraceView(string? Version, IReadOnlyDictionary<string, TraceSelection> Policies, bool Loaded, string? Reason);

    /// <summary>再送時にも変えない比較更新です。</summary>
    private sealed record TraceOperation(string Fingerprint, IReadOnlyList<DurableMutation> Mutations);
}
