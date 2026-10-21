using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>通常操作の永続受付と、実行用の非同期記録を同じ重複照合へ接続します。</summary>
internal sealed class RequestExecutor
{
    private readonly IDurableRepository repository;
    private readonly string scope;
    private readonly string localPeerId;
    private readonly TimeProvider clock;
    private readonly RuntimeRequestExecutor runtime;
    private readonly ConcurrentDictionary<string, byte> bufferedKinds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> deferredKinds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RequestOperation> running = [];
    private readonly ConcurrentDictionary<string, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>>> handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>>> inspections = new(StringComparer.Ordinal);

    /// <summary>Peer固有の実行履歴を指定します。</summary>
    public RequestExecutor(IDurableRepository repository, string peerId, TimeProvider clock)
    {
        this.repository = repository;
        scope = "receipts:" + peerId;
        localPeerId = peerId;
        this.clock = clock;
        runtime = new(repository, scope, clock);
    }

    /// <summary>実行経路の非同期記録待ちと保存問題です。</summary>
    internal object RuntimeStatistics => runtime.Statistics;

    /// <summary>非同期記録を明示した実行中の状態操作を登録します。</summary>
    internal void RegisterRuntime(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler, bool deferRemoteCompletion = false)
    {
        Register(kind, handler, deferRemoteCompletion);
        bufferedKinds.TryAdd(kind, 0);
    }

    /// <summary>副作用を持たない照会処理だけを別に登録します。</summary>
    public void RegisterInspection(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(handler);
        if (!inspections.TryAdd(kind, handler))
        {
            throw new ArgumentException("照会種類は登録済みです。", nameof(kind));
        }
    }

    /// <summary>ノードの具体実行を永続受付後の処理へ接続します。</summary>
    public Func<RemoteExecutionRequest, CancellationToken, Task<NodeResult>>? RemoteExecutionHandler
    {
        get; set;
    }

    /// <summary>指定実体の読取り専用処理を履歴作成なしで呼びます。</summary>
    /// <returns>照会結果。照会失敗から副作用を推測しません。</returns>
    public async Task<ClusterResponse> InspectAsync(ClusterRequest request, Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (request.TargetInstanceId != instanceId || !string.Equals(request.TargetPeerId, localPeerId, StringComparison.Ordinal) || !inspections.TryGetValue(request.Kind, out Func<ClusterRequest, CancellationToken, Task<ClusterResponse>>? handler))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.InspectionTargetUnavailable");
        }
        try
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.InspectionFailed");
        }
    }

    /// <summary>通常要求を登録し、呼出側が元結果を照会できる種類だけ遠隔完了待ちを分離します。</summary>
    public void Register(string kind, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler, bool deferRemoteCompletion = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(handler);
        if (!handlers.TryAdd(kind, handler))
        {
            throw new ArgumentException("処理種類は登録済みです。", nameof(kind));
        }
        if (deferRemoteCompletion)
        {
            deferredKinds.TryAdd(kind, 0);
        }
    }

    /// <summary>登録した種類だけを呼びます。</summary>
    /// <returns>種類固有の結果。</returns>
    public async Task<ClusterResponse> InvokeRegisteredAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        string nodeKind = "node.execute";
        if (string.Equals(request.Kind, nodeKind, StringComparison.Ordinal) && RemoteExecutionHandler is not null)
        {
            RemoteExecutionRequest nodeRequest = request.Payload.Json.Deserialize<RemoteExecutionRequest>() ?? throw new InvalidDataException("要求が不正です。");
            NodeResult result = await RemoteExecutionHandler(nodeRequest, cancellationToken).ConfigureAwait(false);
            return new ClusterResponse(result.Outcome, NodeValue.FromObject(result), result.ReasonCode);
        }
        if (handlers.TryGetValue(request.Kind, out Func<ClusterRequest, CancellationToken, Task<ClusterResponse>>? handler))
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }
        return new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.HandlerMissing");
    }

    /// <summary>同一要求の完了を共有し、新しい副作用実行を増やしません。</summary>
    /// <returns>保存済みまたは今回確認した結果。</returns>
    public Task<ClusterResponse> ExecuteAsync(ClusterRequest request, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler, bool mayStart, bool waitForCompletion, CancellationToken cancellationToken = default)
    {
        return ExecuteCheckedAsync(request, handler, mayStart, waitForCompletion, cancellationToken);
    }

    /// <summary>管理専用受付と通常ノードの参加条件を混同せず永続受付へ渡します。</summary>
    /// <returns>元操作記録または今回の実行結果です。</returns>
    public Task<ClusterResponse> ExecutePeerAsync(ClusterRequest request, ReArtWord.Contents.BackgroundService.Contracts.Cluster.EPeerStatus status, Guid instanceId, bool localCaller,
        Func<ClusterRequest, bool>? detachedAllowed, bool managementEnabled, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.TargetPeerId, localPeerId, StringComparison.Ordinal))
        {
            return Task.FromResult(new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.TargetNotReady"));
        }
        bool localDetached = localCaller && status == ReArtWord.Contents.BackgroundService.Contracts.Cluster.EPeerStatus.Detached && detachedAllowed?.Invoke(request) == true;
        bool management = managementEnabled && string.Equals(request.Kind, ManagementWire.ExecuteKind, StringComparison.Ordinal);
        bool mayStart = request.TargetInstanceId == instanceId && (ReArtWord.Contents.BackgroundService.Contracts.Cluster.PeerStatusRules.CanParticipate(status) || localDetached || management);
        string node = "node.execute";
        return ExecuteAsync(request, InvokeRegisteredAsync, mayStart,
            localCaller || !string.Equals(request.Kind, node, StringComparison.Ordinal) && !deferredKinds.ContainsKey(request.Kind), cancellationToken);
    }

    /// <summary>受信応答とは独立して追跡した実タスクの終了を確認します。</summary>
    /// <returns>全所有実行の完了。</returns>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(running.Values.Select(operation => operation.Completion.Value)).WaitAsync(cancellationToken).ConfigureAwait(false);
        await runtime.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>処理本体を起動せず、保存結果と現実の進行中タスクを照会します。</summary>
    /// <returns>完了・進行中・開始記録のみの区別です。</returns>
    public async Task<ClusterRequestStatus> ReadStatusAsync(RequestQuery query, CancellationToken cancellationToken = default)
    {
        if (runtime.Read(query) is { } buffered)
        {
            return buffered;
        }
        if (running.TryGetValue(query.Id, out RequestOperation? active))
        {
            if (!string.Equals(active.Fingerprint, query.Fingerprint, StringComparison.Ordinal))
            {
                return new ClusterRequestStatus(false, new ClusterResponse(EOutcome.Failed, Reason: "Execution.RequestMismatch"));
            }
            if (!active.Completion.IsValueCreated || !active.Completion.Value.IsCompleted)
            {
                return new ClusterRequestStatus(true);
            }
        }
        DurableEntry? entry = await repository.ReadAsync(scope, query.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
        RequestReceipt? receipt = entry?.Value?.Json.Deserialize<RequestReceipt>();
        if (receipt is null)
        {
            return new ClusterRequestStatus(false, Reason: "Execution.ReceiptMissing");
        }
        if (!string.Equals(receipt.Fingerprint, query.Fingerprint, StringComparison.Ordinal))
        {
            return new ClusterRequestStatus(false, new ClusterResponse(EOutcome.Failed, Reason: "Execution.RequestMismatch"));
        }
        if (receipt.Response is not null)
        {
            return new ClusterRequestStatus(false, receipt.Response);
        }
        return new ClusterRequestStatus(false, Reason: "Execution.StartedWithoutResult");
    }

    /// <summary>内容照合を済ませて同一要求のタスクを共有します。</summary>
    /// <returns>実処理結果。</returns>
    private async Task<ClusterResponse> ExecuteCheckedAsync(ClusterRequest request, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler, bool mayStart, bool waitForCompletion, CancellationToken cancellationToken = default)
    {
        if (running.TryGetValue(request.Id, out RequestOperation? active))
        {
            return await WaitAsync(active, ContractJson.Hash(request), waitForCompletion, cancellationToken).ConfigureAwait(false);
        }
        bool buffered = request.Kind is "node.execute" && request.Payload.Kind == EValueKind.Json
            && request.Payload.Json.TryGetProperty(nameof(RemoteExecutionRequest.BufferedReceipt), out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        if (buffered || bufferedKinds.ContainsKey(request.Kind) || mayStart && !runtime.TryReserveDurable(request))
        {
            return await runtime.ExecuteAsync(request, handler, mayStart, waitForCompletion, cancellationToken).ConfigureAwait(false);
        }
        string fingerprint = ContractJson.Hash(request);
        DurableEntry? entry;
        try
        {
            entry = await repository.ReadAsync(scope, request.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.ReleaseDurableAdmission(request.Id);
        }
        if (entry?.Value is not null)
        {
            RequestReceipt? receipt = entry.Value.Json.Deserialize<RequestReceipt>();
            if (receipt is null || !string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new ClusterResponse(EOutcome.Failed, Reason: "Execution.RequestMismatch");
            }
            if (receipt.Response is not null)
            {
                return receipt.Response;
            }
            if (running.TryGetValue(request.Id, out RequestOperation? pending))
            {
                return await WaitAsync(pending, fingerprint, waitForCompletion, cancellationToken).ConfigureAwait(false);
            }
            return new ClusterResponse(EOutcome.Unknown, Reason: "Execution.StartedWithoutResult");
        }
        if (!mayStart)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.TargetNotReady");
        }
        if (request.ExpiresAt <= clock.GetUtcNow())
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Execution.Expired");
        }
        RequestOperation candidate = new(fingerprint, operation => RunAsync(request, operation, handler));
        return await WaitAsync(running.GetOrAdd(request.Id, candidate), fingerprint, waitForCompletion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>短い実処理は確定結果を直接返し、長い処理には有限時間で受信枠を返します。</summary>
    /// <returns>開始確認または既に完了した結果。</returns>
    private static async Task<ClusterResponse> WaitAsync(RequestOperation operation, string fingerprint, bool waitForCompletion, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "Execution.RequestMismatch");
        }
        Task<ClusterResponse> completion = operation.Completion.Value;
        if (!waitForCompletion && !completion.IsCompleted)
        {
            ClusterResponse admission = await operation.Admission.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await completion.WaitAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return admission;
            }
        }
        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>開始を永続保存してから、依頼元の寿命から独立して処理します。</summary>
    /// <returns>完了確認結果。</returns>
    private async Task<ClusterResponse> RunAsync(ClusterRequest request, RequestOperation operation, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler, CancellationToken cancellationToken = default)
    {
        bool claimed = false;
        ClusterResponse result = new(EOutcome.Unknown, Reason: "Execution.ClaimUnconfirmed");
        try
        {
            string key = request.Id.ToString("N");
            RequestReceipt started = new(operation.Fingerprint, request.TargetInstanceId, request.ExpiresAt, null);
            DurableEntry? claim = await RequestReceiptPersistence.WriteAsync(repository, scope, key, null, NodeValue.FromObject(started), cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                DurableEntry? current = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
                RequestReceipt? receipt = current?.Value?.Json.Deserialize<RequestReceipt>();
                if (receipt is not null)
                {
                    if (!string.Equals(receipt.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                    {
                        return new(EOutcome.Failed, Reason: "Execution.RequestMismatch");
                    }
                    return receipt.Response ?? result;
                }
                return result;
            }
            claimed = true;
            operation.Admission.TrySetResult(new ClusterResponse(EOutcome.Unknown, Reason: "Execution.Running"));
            ClusterResponse response;
            try
            {
                response = await handler(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                response = new ClusterResponse(EOutcome.Unknown, Reason: "Execution.StoppedWithoutConfirmation");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // 接続秘密や入力値を含み得る例外本文を通信結果へ載せません。
                response = new ClusterResponse(EOutcome.Unknown, Reason: "Execution.HandlerFailed");
            }
            DurableEntry? complete = await RequestReceiptPersistence.WriteAsync(repository, scope, key, claim.Version, NodeValue.FromObject(started with { Response = response }), cancellationToken).ConfigureAwait(false);
            if (complete is null)
            {
                return new ClusterResponse(EOutcome.Unknown, Reason: "Execution.CompletionUnconfirmed");
            }
            return response;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (claimed)
            {
                result = new(EOutcome.Unknown, Reason: "Execution.CompletionUnconfirmed");
            }
            return result;
        }
        finally
        {
            operation.Admission.TrySetResult(result);
            running.TryRemove(request.Id, out _);
        }
    }

    /// <summary>同じ指紋の受付通知と完了だけを共有し、保存定期照会と入力を保持するfaulted タスクを残しません。</summary>
    private sealed record RequestOperation
    {
        /// <summary>登録後にだけ開始できる一回の実行を作成します。</summary>
        internal RequestOperation(string fingerprint, Func<RequestOperation, Task<ClusterResponse>> run)
        {
            Fingerprint = fingerprint;
            Completion = new(() => run(this), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>受付前も異内容の同じIDを拒否します。</summary>
        internal string Fingerprint
        {
            get;
        }

        /// <summary>永続受付が成立するまで副作用を開始済みと報告しません。</summary>
        internal TaskCompletionSource<ClusterResponse> Admission
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>依頼元の取消とは独立した所有タスクです。</summary>
        internal Lazy<Task<ClusterResponse>> Completion
        {
            get;
        }
    }
}
