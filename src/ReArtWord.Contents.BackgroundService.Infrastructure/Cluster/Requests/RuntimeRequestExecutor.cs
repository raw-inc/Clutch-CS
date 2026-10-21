using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>固定した起動実体内で一度だけ実行し、完了記録を非同期で永続保存します。</summary>
internal sealed class RuntimeRequestExecutor(IDurableRepository repository, string scope, TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Operation> running = [];
    private readonly Dictionary<Guid, RequestReceipt> completed = [];
    private readonly Dictionary<Guid, string> durableAdmissions = [];
    private readonly RuntimeRequestWindow seen = new();
    private readonly Lazy<RuntimeReceiptWriter> writer = new(() => new(repository, scope));
    private int reserved;
    private long bytes;
    private bool stopping;

    /// <summary>保存状態を管理APIから確認するための小さい統計です。</summary>
    internal object Statistics
    {
        get
        {
            using (gate.EnterScope())
            {
                string? problem = null;
                if (writer.IsValueCreated)
                {
                    problem = writer.Value.Problem;
                }
                return new { running = running.Count, pending = reserved - running.Count, retainedBytes = bytes, knownRequests = seen.Count, stopping, problem };
            }
        }
    }

    /// <summary>同期記録側が先に受け付けたIDも登録し、記録方式を変えた再送で二重実行しません。</summary>
    /// <returns>同期記録側へ初回受付を渡せる場合だけtrueです。</returns>
    internal bool TryReserveDurable(ClusterRequest request)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (!RuntimeRequestIdentity.TryDeadline(request with { ExpiresAt = now }, now, out DateTimeOffset deadline))
        {
            return true;
        }
        using (gate.EnterScope())
        {
            seen.Expire(now);
            if (running.ContainsKey(request.Id) || completed.ContainsKey(request.Id) || seen.Contains(request.Id) || 1048576 <= seen.Count)
            {
                return false;
            }
            seen.Add(request.Id, deadline);
            durableAdmissions.Add(request.Id, ContractJson.Hash(request));
            return true;
        }
    }

    /// <summary>同期保存の照会が終わったら、開始処理または保存済み結果の照合へ所有を戻します。</summary>
    internal void ReleaseDurableAdmission(Guid id)
    {
        using (gate.EnterScope())
        {
            durableAdmissions.Remove(id);
        }
    }

    /// <summary>到着済みIDを共有し、未知IDでも実体・期限・有限枠が成立する場合だけ開始します。</summary>
    /// <returns>元処理の結果、または開始しなかった理由です。</returns>
    internal async Task<ClusterResponse> ExecuteAsync(ClusterRequest request, Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler,
        bool mayStart, bool waitForCompletion, CancellationToken cancellationToken = default)
    {
        string fingerprint = ContractJson.Hash(request);
        Operation? operation;
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            seen.Expire(now);
            if (durableAdmissions.TryGetValue(request.Id, out string? admitting))
            {
                if (!string.Equals(admitting, fingerprint, StringComparison.Ordinal))
                {
                    return new(EOutcome.Failed, Reason: "Execution.RequestMismatch");
                }
                return new(EOutcome.Unknown, Reason: "Execution.Running");
            }
            if (running.TryGetValue(request.Id, out operation))
            {
                if (!string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new(EOutcome.Failed, Reason: "Execution.RequestMismatch");
                }
            }
            if (operation is null && completed.TryGetValue(request.Id, out RequestReceipt? result))
            {
                return Match(result, fingerprint);
            }
            if (operation is null && mayStart && now < request.ExpiresAt && !seen.Contains(request.Id))
            {
                if (!RuntimeRequestIdentity.TryDeadline(request, now, out DateTimeOffset deadline))
                {
                    return new(EOutcome.NotExecuted, Reason: "Execution.RuntimeDeadlineInvalid");
                }
                if (stopping)
                {
                    return new(EOutcome.NotExecuted, Reason: "Execution.Stopping");
                }
                if (512 <= reserved || 1048576 <= seen.Count)
                {
                    return new(EOutcome.NotExecuted, Reason: "Execution.BufferedReceiptCapacity");
                }
                reserved++;
                seen.Add(request.Id, deadline);
                operation = new(fingerprint, new(() => RunAsync(request, fingerprint, handler), LazyThreadSafetyMode.ExecutionAndPublication));
                running.Add(request.Id, operation);
            }
        }
        if (operation is null)
        {
            DurableEntry? stored = await repository.ReadAsync(scope, request.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
            RequestReceipt? receipt = stored?.Value?.Json.Deserialize<RequestReceipt>();
            if (receipt is not null)
            {
                return Match(receipt, fingerprint);
            }
            return new(EOutcome.Unknown, Reason: "Execution.BufferedReceiptUnconfirmed");
        }
        Task<ClusterResponse> resultTask = operation.Completion.Value;
        if (!waitForCompletion)
        {
            try
            {
                return await resultTask.WaitAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new(EOutcome.Unknown, Reason: "Execution.Running");
            }
        }
        return await resultTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>進行中と未保存の完了結果を、ディスク照会なしで取得します。</summary>
    /// <returns>この起動実体のメモリにある場合の照会結果です。</returns>
    internal ClusterRequestStatus? Read(RequestQuery query)
    {
        using (gate.EnterScope())
        {
            if (durableAdmissions.TryGetValue(query.Id, out string? admitting))
            {
                if (!string.Equals(admitting, query.Fingerprint, StringComparison.Ordinal))
                {
                    return new(false, new(EOutcome.Failed, Reason: "Execution.RequestMismatch"));
                }
                return new(true);
            }
            if (completed.TryGetValue(query.Id, out RequestReceipt? receipt))
            {
                return new(false, Match(receipt, query.Fingerprint));
            }
            if (running.TryGetValue(query.Id, out Operation? operation))
            {
                if (!string.Equals(operation.Fingerprint, query.Fingerprint, StringComparison.Ordinal))
                {
                    return new(false, new(EOutcome.Failed, Reason: "Execution.RequestMismatch"));
                }
                return new(true);
            }
        }
        return null;
    }

    /// <summary>全実処理が終了してから完了記録のworkerを閉じます。</summary>
    /// <returns>所有した実処理と記録保存の終了です。</returns>
    internal async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        Operation[] active;
        using (gate.EnterScope())
        {
            stopping = true;
            active = running.Values.ToArray();
        }
        await Task.WhenAll(active.Select(item => item.Completion.Value)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (writer.IsValueCreated)
        {
            await writer.Value.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>呼出元の応答待ち取消とは独立して一度だけ処理し、保存失敗でも副作用を繰り返しません。</summary>
    /// <returns>今回確認した実処理結果です。</returns>
    private async Task<ClusterResponse> RunAsync(ClusterRequest request, string fingerprint,
        Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> handler, CancellationToken cancellationToken = default)
    {
        ClusterResponse result;
        try
        {
            result = await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result = new(EOutcome.Unknown, Reason: "Execution.HandlerFailed");
        }
        ClusterResponse recorded = result;
        long size = 0;
        try
        {
            size = result.Value?.PayloadSize() ?? 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            recorded = new(EOutcome.Unknown, Reason: "Execution.ReceiptDetailOmitted");
        }
        using (gate.EnterScope())
        {
            if (4194304 < size || 67108864 - size < bytes)
            {
                recorded = new(EOutcome.Unknown, Reason: "Execution.ReceiptDetailOmitted");
                size = 0;
            }
            bytes += size;
            RequestReceipt receipt = new(fingerprint, request.TargetInstanceId, request.ExpiresAt, recorded);
            completed.Add(request.Id, receipt);
            running.Remove(request.Id);
            if (!writer.Value.TryWrite(request.Id, receipt, saved => Release(request.Id, size, saved)))
            {
                Release(request.Id, size, false);
            }
        }
        return result;
    }

    /// <summary>保存後に重い結果を解放し、IDだけを元の受付期限まで保持します。</summary>
    private void Release(Guid id, long size, bool saved)
    {
        using (gate.EnterScope())
        {
            bytes -= size;
            reserved--;
            completed.Remove(id);
        }
    }

    /// <summary>同じIDの異内容を確定結果へ混ぜません。</summary>
    /// <returns>一致した元結果または内容不一致です。</returns>
    private static ClusterResponse Match(RequestReceipt receipt, string fingerprint)
    {
        if (!string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new(EOutcome.Failed, Reason: "Execution.RequestMismatch");
        }
        return receipt.Response ?? new(EOutcome.Unknown, Reason: "Execution.BufferedReceiptUnconfirmed");
    }

    /// <summary>同一実体内の同時到着を一つの所有タスクへ合流します。</summary>
    private sealed record Operation(string Fingerprint, Lazy<Task<ClusterResponse>> Completion);
}
