using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>元の実行に対する取消だけを受け付け、その操作結果を再照会できるようにします。</summary>
internal sealed class WorkflowRunCancellation : IDisposable
{
    private readonly IDurableRepository repository;
    private readonly IPeerDirectory peers;
    private readonly Func<Guid, CancellationToken, Task<WorkflowRunStatus?>> read;
    private readonly Func<Guid, CancellationToken, Task<bool>> cancel;
    private readonly SemaphoreSlim admission = new(1, 1);
    private string Scope => "peer:" + peers.LocalPeerId + ":run-cancellations";

    /// <summary>同じ実行追跡とローカルの操作記録を使用します。</summary>
    internal WorkflowRunCancellation(IDurableRepository repository, IPeerDirectory peers,
        Func<Guid, CancellationToken, Task<WorkflowRunStatus?>> read, Func<Guid, CancellationToken, Task<bool>> cancel)
    {
        this.repository = repository;
        this.peers = peers;
        this.read = read;
        this.cancel = cancel;
    }

    /// <summary>取消要求を記録してから、元の実行だけに取消を伝えます。</summary>
    /// <returns>取消操作と元の実行を区別した結果。</returns>
    internal async Task<WorkflowCancelResult> CancelAsync(WorkflowCancelRequest request, CancellationToken cancellationToken = default)
    {
        string fingerprint = ContractJson.Hash(request);
        string key = request.OperationId.ToString("N");
        string? version;
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableEntry? previous = await repository.ReadAsync(Scope, key, cancellationToken).ConfigureAwait(false);
            if (previous?.Value is not null)
            {
                CancellationRecord known = previous.Value.Json.Deserialize<CancellationRecord>(ContractJson.Options)!;
                if (!string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new(request.OperationId, request.RunId, EOutcome.NotExecuted, false, Reason: "run.cancelRequestMismatch");
                }
                return known.Result;
            }
            WorkflowRunStatus? current = await read(request.RunId, cancellationToken).ConfigureAwait(false);
            if (request.OperationId == Guid.Empty || request.RunId == Guid.Empty || request.OwnerInstanceId != peers.LocalInstanceId
                || current?.OwnerInstanceId != peers.LocalInstanceId || !string.Equals(current.PeerId, peers.LocalPeerId, StringComparison.Ordinal))
            {
                return new(request.OperationId, request.RunId, EOutcome.NotExecuted, false, current?.Running, current?.Outcome, "run.cancelOwnerMismatch");
            }
            WorkflowCancelResult pending = new(request.OperationId, request.RunId, EOutcome.Unknown, false, current.Running, current.Outcome, "run.cancelPending");
            DurableReceipt begun = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(Scope, key, null,
                NodeValue.FromObject(new CancellationRecord(fingerprint, pending)))], cancellationToken).ConfigureAwait(false);
            if (begun.Outcome != EOutcome.Succeeded)
            {
                return pending with { Reason = "run.cancelNotRecorded" };
            }
            version = begun.Entries[0].Version;
        }
        finally
        {
            admission.Release();
        }
        WorkflowCancelResult result = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            DurableReceipt recorded = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(Scope, key, version,
                NodeValue.FromObject(new CancellationRecord(fingerprint, result)))], CancellationToken.None).ConfigureAwait(false);
            if (recorded.Outcome != EOutcome.Succeeded)
            {
                result = result with { Outcome = EOutcome.Unknown, Reason = "run.cancelResultNotRecorded" };
            }
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or InvalidOperationException)
        {
            result = result with { Outcome = EOutcome.Unknown, Reason = "run.cancelResultNotRecorded" };
        }
        return result;
    }

    /// <summary>完了済み実行を再開せず、実行中の元要求だけへ取消を伝えます。</summary>
    /// <returns>今回確認できた受付と実行状態。</returns>
    private async Task<WorkflowCancelResult> RequestAsync(WorkflowCancelRequest request, CancellationToken cancellationToken = default)
    {
        bool requested = false;
        try
        {
            WorkflowRunStatus? current = await read(request.RunId, cancellationToken).ConfigureAwait(false);
            if (current?.Running != true)
            {
                if (current is null || current.Outcome == EOutcome.Unknown)
                {
                    return new(request.OperationId, request.RunId, EOutcome.NotExecuted, false, current?.Running, current?.Outcome, "run.cancellationUnavailable");
                }
                return new(request.OperationId, request.RunId, EOutcome.Succeeded, false, current.Running, current.Outcome, "run.alreadyFinished");
            }
            requested = true;
            bool accepted = await cancel(request.RunId, cancellationToken).ConfigureAwait(false);
            current = await read(request.RunId, cancellationToken).ConfigureAwait(false);
            EOutcome outcome = EOutcome.Unknown;
            string reason = "run.cancelUnconfirmed";
            if (accepted)
            {
                outcome = EOutcome.Succeeded;
                reason = "run.cancellationRequested";
            }
            return new(request.OperationId, request.RunId, outcome, requested, current?.Running, current?.Outcome, reason);
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or OperationCanceledException or InvalidOperationException)
        {
            return new(request.OperationId, request.RunId, EOutcome.Unknown, requested, Reason: "run.cancelUnconfirmed");
        }
    }

    /// <summary>元の取消操作を再実行せず、保持した結果だけを取得します。</summary>
    /// <returns>記録した取消結果。未受付の場合はnull。</returns>
    internal async Task<WorkflowCancelResult?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<CancellationRecord>(ContractJson.Options)?.Result;
    }

    /// <summary>管理要求の終了後に、取消受付用の排他を解放します。</summary>
    public void Dispose() => admission.Dispose();

    /// <summary>要求の同一性と、再照会時に返す結果を保持します。</summary>
    /// <param name="Fingerprint">元の取消要求の内容識別子。</param>
    /// <param name="Result">確認した取消操作の結果。</param>
    private sealed record CancellationRecord(string Fingerprint, WorkflowCancelResult Result);
}
