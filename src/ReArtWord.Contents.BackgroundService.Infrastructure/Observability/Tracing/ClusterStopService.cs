using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Tracing;

/// <summary>人の追跡解除と元の実体への停止要求を接続し、全停止の観測を捏造しません。</summary>
public sealed class ClusterStopService
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly TraceService trace;
    private readonly ConcurrentDictionary<Guid, ClusterRequest> pending = [];
    private int requested;
    private static string Kind => "trace.cluster-stop";
    private static string Scope => "trace:cluster-stop";

    /// <summary>既存操作記録経路へ停止受付だけを登録します。</summary>
    public ClusterStopService(PeerCluster cluster, IDurableRepository repository, TraceService trace)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(trace);
        this.cluster = cluster;
        this.repository = repository;
        this.trace = trace;
        cluster.RegisterHandler(Kind, AcceptAsync);
    }

    /// <summary>永続受付後にHostの通常停止へ接続します。</summary>
    public Action? StopHost
    {
        get; set;
    }

    /// <summary>この実体へ通常停止を要求済みか。</summary>
    public bool StopRequested => Volatile.Read(ref requested) != 0;

    /// <summary>固定対象の全保存確認後にだけ、元の実体へ停止を要求します。</summary>
    /// <returns>停止要求受付までの対象別結果です。</returns>
    public async Task<TraceClusterStopResult> RequestAsync(TraceClusterStopRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = request.OperationId.ToString("N");
        DurableEntry? previous = await repository.ReadAsync(Scope, key, cancellationToken).ConfigureAwait(false);
        StopOperation? operation = previous?.Value?.Json.Deserialize<StopOperation>(ContractJson.Options);
        if (operation is not null)
        {
            if (!string.Equals(ContractJson.Hash(operation.Request), ContractJson.Hash(request), StringComparison.Ordinal))
            {
                return Empty(request.OperationId, EOutcome.NotExecuted, "ClusterStop.OperationMismatch");
            }
            return await GetOperationAsync(request.OperationId, cancellationToken).ConfigureAwait(false)
                ?? Empty(request.OperationId, EOutcome.Unknown, "ClusterStop.OriginalOperationUnconfirmed");
        }
        PeerInfo[] members = cluster.GetPeers().Where(peer => peer.Status != EPeerStatus.Detached).OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();
        TraceStopTarget[] targets = members.Select(peer => new TraceStopTarget(peer.PeerId, peer.InstanceId)).ToArray();
        if (members.Length == 0 || members.Any(peer => !PeerStatusRules.CanParticipate(peer.Status))
            || !string.Equals(ContractJson.Hash(targets), ContractJson.Hash(request.Targets.OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray()), StringComparison.Ordinal))
        {
            return Empty(request.OperationId, EOutcome.NotExecuted, "ClusterStop.MembershipUnconfirmed");
        }
        Guid clearId = Identity(request.OperationId, "clear");
        ClusterRequest[] commands = targets.Select(target => new ClusterRequest(Identity(request.OperationId, target.PeerId), Kind, target.PeerId, target.InstanceId,
            DateTimeOffset.UtcNow.AddMinutes(2), NodeValue.FromObject(new StopCommand(request.OperationId, clearId)))).ToArray();
        operation = new(request, members, commands);
        DurableReceipt journal = await cluster.ReplicateFixedAsync(Identity(request.OperationId, "journal"), [new DurableMutation(Scope, key, null, NodeValue.FromObject(operation))], members, cancellationToken).ConfigureAwait(false);
        if (journal.Outcome != EOutcome.Succeeded)
        {
            return Empty(request.OperationId, journal.Outcome, "ClusterStop.JournalUnconfirmed");
        }
        DurableReceipt cleared = await trace.ClearAsync(clearId, request.ExpectedTraceVersion, members, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (cleared.Outcome != EOutcome.Succeeded)
        {
            return await SaveAsync(new(request.OperationId, cleared.Outcome, cleared.Outcome, [], "ClusterStop.TraceClearUnconfirmed"), cancellationToken).ConfigureAwait(false);
        }
        List<TraceStopTargetResult> results = [];
        foreach (ClusterRequest command in commands.OrderBy(command => string.Equals(command.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal)))
        {
            ClusterResponse accepted = await cluster.RequestAsync(command, cancellationToken).ConfigureAwait(false);
            results.Add(new(new(command.TargetPeerId, command.TargetInstanceId), accepted.Outcome, accepted.Reason));
        }
        EOutcome outcome = EOutcome.Succeeded;
        if (results.Any(result => result.Acceptance != EOutcome.Succeeded))
        {
            outcome = EOutcome.Unknown;
        }
        return await SaveAsync(new(request.OperationId, outcome, EOutcome.Succeeded, results, "ClusterStop.RequestsOnlyNotProcessExitConfirmation"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>元操作記録だけを読み、停止命令の再作成や別実体への再送をしません。</summary>
    /// <returns>保存済み結果または未受理のnullです。</returns>
    public async Task<TraceClusterStopResult?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? result = await repository.ReadAsync(LocalScope(), operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (result?.Value is not null)
        {
            return result.Value.Json.Deserialize<TraceClusterStopResult>(ContractJson.Options);
        }
        DurableEntry? journal = await repository.ReadAsync(Scope, operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (journal?.Value is null)
        {
            return null;
        }
        StopOperation operation = journal.Value.Json.Deserialize<StopOperation>(ContractJson.Options)!;
        List<TraceStopTargetResult> results = [];
        foreach (ClusterRequest command in operation.Commands)
        {
            ClusterRequestStatus status = await cluster.QueryRequestAsync(command, cancellationToken).ConfigureAwait(false);
            results.Add(new(new(command.TargetPeerId, command.TargetInstanceId), status.Response?.Outcome ?? EOutcome.Unknown, status.Reason ?? status.Response?.Reason));
        }
        DurableReceipt? cleared = await repository.ReadReceiptAsync(Identity(operationId, "clear"), cancellationToken).ConfigureAwait(false);
        return new(operationId, EOutcome.Unknown, cleared?.Outcome ?? EOutcome.Unknown, results, "ClusterStop.ProcessExitNotConfirmed");
    }

    /// <summary>操作記録の完了保存を確認してからHostの停止を一度だけ要求します。</summary>
    /// <returns>停止要求の確認完了です。</returns>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (KeyValuePair<Guid, ClusterRequest> item in pending)
        {
            ClusterRequestStatus status = await cluster.QueryRequestAsync(item.Value, cancellationToken).ConfigureAwait(false);
            if (status.Response?.Outcome == EOutcome.Succeeded && StopHost is not null && Interlocked.Exchange(ref requested, 1) == 0)
            {
                pending.TryRemove(item.Key, out _);
                StopHost();
            }
        }
    }

    /// <summary>追跡解除をこの実体でも永続確認してから停止受付を返します。</summary>
    /// <returns>Host停止そのものとは区別した受付です。</returns>
    private async Task<ClusterResponse> AcceptAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        StopCommand command = request.Payload.Json.Deserialize<StopCommand>(ContractJson.Options) ?? throw new InvalidDataException("停止要求が不正です。");
        DurableEntry? journal = await repository.ReadAsync(Scope, command.OperationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        StopOperation? operation = journal?.Value?.Json.Deserialize<StopOperation>(ContractJson.Options);
        Guid expectedClear = Identity(command.OperationId, "clear");
        if (command.ClearOperationId != expectedClear || operation is null
            || !operation.Commands.Any(saved => string.Equals(ContractJson.Hash(saved), ContractJson.Hash(request), StringComparison.Ordinal)))
        {
            return new(EOutcome.NotExecuted, Reason: "ClusterStop.OriginalRequestMissing");
        }
        DurableReceipt? receipt = await repository.ReadReceiptAsync(command.ClearOperationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Outcome != EOutcome.Succeeded || StopHost is null)
        {
            return new(EOutcome.NotExecuted, Reason: "ClusterStop.ClearOrHostUnavailable");
        }
        pending.TryAdd(request.Id, request);
        return new(EOutcome.Succeeded, Reason: "ClusterStop.Accepted");
    }

    /// <summary>元要求の結果だけをローカル診断行へ原子的に保存します。</summary>
    /// <returns>保存した結果です。</returns>
    private async Task<TraceClusterStopResult> SaveAsync(TraceClusterStopResult result, CancellationToken cancellationToken = default)
    {
        EOutcome saved = await repository.WriteLocalSnapshotAsync(LocalScope(), result.OperationId.ToString("N"), NodeValue.FromObject(result), cancellationToken).ConfigureAwait(false);
        if (saved != EOutcome.Succeeded)
        {
            return result with { Outcome = EOutcome.Unknown, Reason = "ClusterStop.ResultNotRecorded" };
        }
        return result;
    }

    /// <summary>このPeerの問い合わせ結果を共有制御データと分けます。</summary>
    /// <returns>ローカル保存スコープです。</returns>
    private string LocalScope() => "peer:" + cluster.LocalPeerId + ":cluster-stop-results";

    /// <summary>段階ごとに元操作から一定の識別子を作ります。</summary>
    /// <returns>段階識別子です。</returns>
    private static Guid Identity(Guid id, string phase) => new(Convert.FromHexString(ContractJson.Hash(new { id, phase })).AsSpan(0, 16));

    /// <summary>副作用を実施していない結果です。</summary>
    /// <returns>対象未処理の結果です。</returns>
    private static TraceClusterStopResult Empty(Guid id, EOutcome outcome, string reason) => new(id, outcome, EOutcome.NotExecuted, [], reason);

    /// <summary>停止対象と要求期限を初回から変えない操作記録です。</summary>
    private sealed record StopOperation(TraceClusterStopRequest Request, IReadOnlyList<PeerInfo> Members, IReadOnlyList<ClusterRequest> Commands);

    /// <summary>追跡解除の確認元を指す停止要求です。</summary>
    private sealed record StopCommand(Guid OperationId, Guid ClearOperationId);
}
