using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>開始前の予定待ち、元要求への取消、実際の配送結果を一つの境界で扱います。</summary>
internal sealed class WorkflowNodeDispatch(IRemoteNodeDispatcher dispatcher, WorkflowExecutionOwnership ownership, ScheduledTargetReadiness schedules,
    IWorkflowLog? log, string peerId, TimeProvider clock)
{
    /// <summary>補償可能なファイル操作を全対象一括で実行する先。</summary>
    internal IFileTransactionDispatcher? FileTransactions
    {
        get; set;
    }

    /// <summary>予定入力の配送許可を、実際の配送より前に確定する処理。</summary>
    internal Func<RemoteExecutionRequest, CancellationToken, ValueTask<NodeResult?>>? BeforeScheduledDispatch
    {
        get; set;
    }

    /// <summary>待受寿命を引き継ぐ先の動作契約を確認します。</summary>
    internal Func<RemoteExecutionRequest, CancellationToken, ValueTask<NodeResult?>>? ScopedDispatchCheck
    {
        get; set;
    }

    /// <summary>開始可能になるまでの待機と、開始後の結果不明を区別して配送します。</summary>
    /// <returns>元の一件に属する対象別結果。</returns>
    // 抑制理由: 配送元のノード情報を記録した後、従来どおり所有境界へ例外を伝えます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "実行先未確認の例外を記録し、元の例外と実行結果を維持します。")]
    internal async Task<TargetNodeResult> ExecuteAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (await CheckScopeAsync(request, cancellationToken).ConfigureAwait(false) is { } rejected)
        {
            return new(request.TargetPeerId, rejected);
        }
        WorkflowExecutionCancellation? input = ownership.Find(request.CausationId);
        if (input?.Schedule is not null)
        {
            ScheduledTargetPreparation prepared = await schedules.PrepareAsync(request, () => input.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
            if (prepared.Rejection is not null)
            {
                return new(request.TargetPeerId, prepared.Rejection);
            }
            request = prepared.Request;
            if (BeforeScheduledDispatch is not null && await BeforeScheduledDispatch(request, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return new(request.TargetPeerId, refused);
            }
        }
        if (input is not null && !input.Register(request))
        {
            return new(request.TargetPeerId, NodeResult.Failure("source.cancelledBeforeExecution", "元の入力の明示取消後は新しい処理を開始しません。", EOutcome.NotExecuted));
        }
        NodeResult result;
        try
        {
            result = await dispatcher.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = NodeResult.Failure("execution.unconfirmed", "取消要求後の実行先の停止を確認できません。", EOutcome.Unknown);
        }
        catch (IOException error)
        {
            result = NodeResult.Failure("transport.unavailable", "実行先の結果を取得できません。", EOutcome.Unknown);
            await LogFailureAsync(request, result, error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await LogFailureAsync(request, NodeResult.Failure("execution.dispatchException", "ノードの実行結果を取得できません。", EOutcome.Unknown), error, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            input?.Complete(request.Id);
        }
        return new(request.TargetPeerId, result);
    }

    /// <summary>配送自体の例外にも、元の実行・ノード・対象Peerを残します。</summary>
    /// <returns>共通の保護境界を通す記録試行です。</returns>
    private ValueTask LogFailureAsync(RemoteExecutionRequest request, NodeResult result, Exception error, CancellationToken cancellationToken = default)
        => WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), clock.GetUtcNow(), "ERROR", peerId, result.ReasonCode!, request.WorkflowId,
            request.Node.Id, request.CausationId, NodeExecutionLog.Result(request.Id, request.Revision, result, error, request.TargetPeerId), request.ContainsPersonalData, Hooks: request.Hooks), cancellationToken);

    /// <summary>通常配送と一括配送のどちらでも、所有情報を保持できる実体だけへ渡します。</summary>
    /// <returns>対応していない配送先の拒否、またはnullです。</returns>
    private ValueTask<NodeResult?> CheckScopeAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SourceScopes is not { Count: > 0 })
        {
            return ValueTask.FromResult<NodeResult?>(null);
        }
        return ScopedDispatchCheck?.Invoke(request, cancellationToken) ?? ValueTask.FromResult<NodeResult?>(
            NodeResult.Failure("source.scope.unavailable", "The execution target cannot preserve source ownership.", EOutcome.NotExecuted));
    }

    /// <summary>全体補償を選んだファイル処理は、全対象の開始条件が揃ってから開始します。</summary>
    /// <returns>実行・補償の対象別結果。</returns>
    internal async Task<ExecutionFrame> FilesAsync(string nodeId, Guid causationId, IReadOnlyList<RemoteExecutionRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (RemoteExecutionRequest request in requests)
        {
            if (await CheckScopeAsync(request, cancellationToken).ConfigureAwait(false) is { } rejected)
            {
                return new(nodeId, rejected.Outcome, requests.Select(target => new TargetNodeResult(target.TargetPeerId, rejected)).ToArray());
            }
        }
        WorkflowExecutionCancellation? input = ownership.Find(causationId);
        IReadOnlyList<RemoteExecutionRequest> fixedRequests = requests;
        if (input?.Schedule is not null)
        {
            ScheduledTargetPreparation[] prepared = await Task.WhenAll(requests.Select(request => schedules.PrepareAsync(request,
                () => input.IsCancellationRequested, cancellationToken))).ConfigureAwait(false);
            if (prepared.Any(item => item.Rejection is not null))
            {
                TargetNodeResult[] results = prepared.Select(item => new TargetNodeResult(item.Request.TargetPeerId, item.Rejection
                    ?? NodeResult.Failure("file.targetsNotReady", "同じ全体補償操作の対象が揃わないため、ファイルを変更しません。", EOutcome.NotExecuted))).ToArray();
                return new(nodeId, ExecutionOutcomes.Aggregate(results.Select(item => item.Result.Outcome)), results);
            }
            fixedRequests = prepared.Select(item => item.Request).ToArray();
            if (BeforeScheduledDispatch is not null && fixedRequests.Count != 0
                && await BeforeScheduledDispatch(fixedRequests[0], cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return new(nodeId, refused.Outcome, fixedRequests.Select(request => new TargetNodeResult(request.TargetPeerId, refused)).ToArray());
            }
        }
        try
        {
            if (input is not null && fixedRequests.Any(request => !input.Register(request)))
            {
                return new(nodeId, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("source.cancelledBeforeExecution", "元の入力の明示取消後は新しい処理を開始しません。", EOutcome.NotExecuted))]);
            }
            FileTransactionResult transaction = await FileTransactions!.ExecuteAsync(new FileTransactionRequest(Guid.NewGuid(), fixedRequests), cancellationToken).ConfigureAwait(false);
            return new(nodeId, transaction.Outcome, transaction.Targets.Select(static item => new TargetNodeResult(item.PeerId, item.Execution)).ToArray(), FileTransaction: transaction);
        }
        finally
        {
            foreach (RemoteExecutionRequest request in fixedRequests)
            {
                input?.Complete(request.Id);
            }
        }
    }
}
