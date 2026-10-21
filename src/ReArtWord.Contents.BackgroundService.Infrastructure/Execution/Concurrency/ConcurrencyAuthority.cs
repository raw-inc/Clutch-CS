using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;

/// <summary>現役の占有だけを共有保存し、待機順と表示は担当のメモリで管理します。</summary>
internal sealed class ConcurrencyAuthority(PeerCluster cluster, IDurableRepository repository,
    Func<IReadOnlyList<WorkflowConcurrencyRequest>, CancellationToken, Task<IReadOnlyDictionary<Guid, bool>>> readOwners, TimeProvider clock) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<WorkflowConcurrencyTarget, ConcurrencyGateQueue> queues = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closing;
    private readonly ConcurrencyPermitStore permits = new(cluster, repository);

    /// <summary>同じノードの先頭だけが空き枠を取得し、取消と期限を待機中も確認します。</summary>
    /// <returns>共有確認済みの枠、または後続を開始しない理由です。</returns>
    // 抑制理由: Changedはキューが所有する通知Taskを借用し、排他の外で同じ世代をawaitします。待機者から破棄しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "通知Taskは共有キューが所有し、待機者は借用だけを行います。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "排他内で通知世代を固定し、排他外のWaitAsyncで必ずawaitします。")]
    internal async Task<ClusterResponse> AcquireAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        WorkflowConcurrencyRequest request = ReadRequest(command);
        WorkflowConcurrencyTarget target = new(request.WorkflowId, request.NodeId);
        using ConcurrencyGateQueue? queue = Borrow(target);
        if (queue is null)
        {
            return new(EOutcome.Failed, Reason: "concurrency.capacity");
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        long started = clock.GetTimestamp();
        DateTimeOffset queuedAt = clock.GetUtcNow(), nextOwnership = DateTimeOffset.MinValue;
        bool registered = false, confirmedOwner = false;
        DateTimeOffset? deadline = null;
        if (request.WaitTimeoutMilliseconds != 0)
        {
            deadline = queuedAt.AddMilliseconds(request.WaitTimeoutMilliseconds);
        }
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Task changed;
                await queue.Changes.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (!MayWrite())
                    {
                        return new(EOutcome.NotExecuted, Reason: "concurrency.coordinatorChanged");
                    }
                    await permits.RestoreAsync(target, queue, lifetime.Token).ConfigureAwait(false);
                    ConcurrencyPermit? known = queue.Active.FirstOrDefault(permit => permit.Request.TicketId == request.TicketId);
                    if (known is not null)
                    {
                        return new(EOutcome.Succeeded, NodeValue.FromObject(known));
                    }
                    if (registered && !queue.Waiters.ContainsKey(request.TicketId))
                    {
                        return new(EOutcome.Cancelled, Reason: "concurrency.cancelled");
                    }
                    if (!registered && 4096 <= queue.Waiting.Count)
                    {
                        return new(EOutcome.Failed, Reason: "concurrency.queueFull");
                    }
                    queue.Enqueue(new(request.TicketId, request.RunId, request.OwnerPeerId, queuedAt, deadline, request.ProceedOnTimeout));
                    registered = true;
                    bool elapsed = request.WaitTimeoutMilliseconds != 0 && request.WaitTimeoutMilliseconds <= clock.GetElapsedTime(started).TotalMilliseconds;
                    bool available = queue.Active.Count < request.MaximumConcurrency && queue.Waiting.First?.Value.TicketId == request.TicketId;
                    if (confirmedOwner && (available || elapsed && request.ProceedOnTimeout))
                    {
                        if (4096 <= queue.Active.Count)
                        {
                            return new(EOutcome.Failed, Reason: "concurrency.capacity");
                        }
                        ConcurrencyPermit permit = new(request, clock.GetUtcNow(), !available);
                        ClusterResponse saved = await permits.SaveAsync(target, queue, [.. queue.Active, permit], command.Id, lifetime.Token).ConfigureAwait(false);
                        if (saved.Outcome != EOutcome.Succeeded)
                        {
                            return saved;
                        }
                        return new(EOutcome.Succeeded, NodeValue.FromObject(permit));
                    }
                    if (elapsed)
                    {
                        return new(EOutcome.Failed, Reason: "concurrency.timeout");
                    }
                    changed = queue.Changed;
                }
                finally
                {
                    queue.Changes.Release();
                }
                if (nextOwnership <= clock.GetUtcNow())
                {
                    nextOwnership = clock.GetUtcNow().AddSeconds(5);
                    double remaining = request.WaitTimeoutMilliseconds == 0 ? 2000
                                                                            : Math.Clamp(request.WaitTimeoutMilliseconds - clock.GetElapsedTime(started).TotalMilliseconds, 1, 2000);
                    bool? present = await RecoverAsync(target, queue, request, TimeSpan.FromMilliseconds(remaining), lifetime.Token).ConfigureAwait(false);
                    if (present == false)
                    {
                        return new(EOutcome.Cancelled, Reason: "concurrency.cancelled");
                    }
                    if (present == true)
                    {
                        confirmedOwner = true;
                        continue;
                    }
                }
                double milliseconds = 1000;
                if (request.WaitTimeoutMilliseconds != 0)
                {
                    milliseconds = Math.Clamp(request.WaitTimeoutMilliseconds - clock.GetElapsedTime(started).TotalMilliseconds, 1, milliseconds);
                }
                try
                {
                    await changed.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), clock, lifetime.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 通知がなくても、所有元の終了と待機期限だけを有限間隔で確認します。
                }
            }
            return new(EOutcome.Cancelled, Reason: "concurrency.cancelled");
        }
        finally
        {
            await queue.Changes.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                queue.Remove(request.TicketId);
            }
            finally
            {
                queue.Changes.Release();
            }
        }
    }

    /// <summary>元の所有識別が一致する枠と、その受付の待機だけを解放します。</summary>
    /// <returns>共有保存の確認結果です。</returns>
    internal async Task<ClusterResponse> ReleaseAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        WorkflowConcurrencyRequest request = ReadRequest(command);
        WorkflowConcurrencyTarget target = new(request.WorkflowId, request.NodeId);
        using ConcurrencyGateQueue? queue = Borrow(target);
        if (queue is null)
        {
            return new(EOutcome.NotExecuted, Reason: "concurrency.capacity");
        }
        await queue.Changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!MayWrite())
            {
                return new(EOutcome.NotExecuted, Reason: "concurrency.coordinatorChanged");
            }
            await permits.RestoreAsync(target, queue, cancellationToken).ConfigureAwait(false);
            queue.Remove(request.TicketId);
            ConcurrencyPermit[] remaining = queue.Active.Where(permit => permit.Request != request).ToArray();
            if (remaining.Length == queue.Active.Count)
            {
                return new(EOutcome.Succeeded);
            }
            return await permits.SaveAsync(target, queue, remaining, command.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            queue.Changes.Release();
        }
    }

    /// <summary>現役枠と先頭50件だけを表示へ渡し、照会による保存を行いません。</summary>
    /// <returns>要求されたノードだけの現在表示です。</returns>
    internal async Task<ClusterResponse> ReadAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        WorkflowConcurrencyTarget[] targets = command.Payload.Json.Deserialize<WorkflowConcurrencyTarget[]>() ?? throw new InvalidDataException("待機一覧の対象がありません。");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targets.Length, 100);
        List<WorkflowConcurrencySnapshot> snapshots = [];
        foreach (WorkflowConcurrencyTarget target in targets)
        {
            using ConcurrencyGateQueue? queue = Borrow(target);
            if (queue is null)
            {
                snapshots.Add(new(target.WorkflowId, target.NodeId, 0, 0, [], clock.GetUtcNow(), "concurrency.capacity"));
                continue;
            }
            await queue.Changes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!MayWrite())
                {
                    return new(EOutcome.NotExecuted, Reason: "concurrency.coordinatorChanged");
                }
                await permits.RestoreAsync(target, queue, cancellationToken).ConfigureAwait(false);
                snapshots.Add(new(target.WorkflowId, target.NodeId, queue.Active.Count, queue.Waiting.Count, queue.Waiting.Take(50).ToArray(), clock.GetUtcNow()));
            }
            finally
            {
                queue.Changes.Release();
            }
        }
        return new(EOutcome.Succeeded, NodeValue.FromObject(snapshots));
    }

    /// <summary>所有確認の通信を排他外でまとめ、低速なPeerで解放や表示を止めません。</summary>
    /// <returns>受付元の終了を確認できた場合はfalse、未確認ならnullです。</returns>
    private async Task<bool?> RecoverAsync(WorkflowConcurrencyTarget target, ConcurrencyGateQueue queue, WorkflowConcurrencyRequest request,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        WorkflowConcurrencyRequest[] checking;
        await queue.Changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await permits.RestoreAsync(target, queue, cancellationToken).ConfigureAwait(false);
            checking = [request];
            if (queue.NextRecovery <= clock.GetUtcNow())
            {
                queue.NextRecovery = clock.GetUtcNow().AddSeconds(5);
                checking = queue.Active.Select(permit => permit.Request).Append(request).DistinctBy(value => value.TicketId).ToArray();
            }
        }
        finally
        {
            queue.Changes.Release();
        }
        IReadOnlyDictionary<Guid, bool> ownership;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        try
        {
            ownership = await readOwners(checking, timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        if (ownership.Values.Any(value => !value))
        {
            await queue.Changes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (MayWrite())
                {
                    await permits.RestoreAsync(target, queue, cancellationToken).ConfigureAwait(false);
                    ConcurrencyPermit[] remaining = queue.Active.Where(permit => !ownership.TryGetValue(permit.Request.TicketId, out bool present) || present).ToArray();
                    if (remaining.Length != queue.Active.Count)
                    {
                        _ = await permits.SaveAsync(target, queue, remaining, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                queue.Changes.Release();
            }
        }
        return ownership.TryGetValue(request.TicketId, out bool active) ? active
                                                                        : null;
    }

    /// <summary>読み取りや待機が残る区間を途中で破棄しません。</summary>
    /// <returns>所有数を増やしたキュー、または保持上限のnullです。</returns>
    private ConcurrencyGateQueue? Borrow(WorkflowConcurrencyTarget target)
    {
        using (gate.EnterScope())
        {
            if (closing)
            {
                return null;
            }
            if (!queues.TryGetValue(target, out ConcurrencyGateQueue? queue))
            {
                if (1024 <= queues.Count || stopping.IsCancellationRequested)
                {
                    return null;
                }
                queues[target] = queue = new(value => Return(target, value));
            }
            queue.Users++;
            return queue;
        }
    }

    /// <summary>完了済みで誰も参照しないキューをその場で除去します。</summary>
    private void Return(WorkflowConcurrencyTarget target, ConcurrencyGateQueue queue)
    {
        using (gate.EnterScope())
        {
            queue.Users--;
            if (queue.Users == 0 && (closing || queue.Active.Count == 0 && queue.Waiting.Count == 0))
            {
                queues.Remove(target);
                queue.Close();
            }
            if (closing && queues.Count == 0)
            {
                drained.TrySetResult();
            }
        }
    }

    /// <summary>通常参加中の現担当だけがクラスタ共通の枠を変更します。</summary>
    /// <returns>この実体で受付可能ならtrueです。</returns>
    private bool MayWrite() => !Volatile.Read(ref closing) && !cluster.IsDetached && string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal)
        && cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status));

    /// <summary>受付前に所有識別と設定の範囲を確認します。</summary>
    /// <returns>検証した元の要求です。</returns>
    private static WorkflowConcurrencyRequest ReadRequest(ClusterRequest command)
    {
        WorkflowConcurrencyRequest request = command.Payload.Json.Deserialize<WorkflowConcurrencyRequest>() ?? throw new InvalidDataException("制限区間の要求がありません。");
        if (request.TicketId == Guid.Empty || request.RunId == Guid.Empty || request.OwnerInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(request.OwnerPeerId)
            || string.IsNullOrWhiteSpace(request.WorkflowId) || string.IsNullOrWhiteSpace(request.NodeId) || request.MaximumConcurrency is < 1 or > 1024 || request.WaitTimeoutMilliseconds < 0)
        {
            throw new InvalidDataException("制限区間の所有者または設定が不正です。");
        }
        return request;
    }

    /// <summary>新規受付を閉じ、実行中の待機が終了してから各キューを片付けます。</summary>
    /// <returns>待機者を回収し、取消資源を解放するまでの処理です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名であり、開始済みの参照を回収してから資源を解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名で、終了待ちを外側の取消により省略しません。")]
    public async ValueTask DisposeAsync()
    {
        bool first;
        using (gate.EnterScope())
        {
            first = !closing;
            closing = true;
            foreach ((WorkflowConcurrencyTarget target, ConcurrencyGateQueue queue) in queues.ToArray())
            {
                if (queue.Users == 0)
                {
                    queues.Remove(target);
                    queue.Close();
                }
            }
            if (queues.Count == 0)
            {
                drained.TrySetResult();
            }
        }
        if (first)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }
        await drained.Task.ConfigureAwait(false);
        if (first)
        {
            stopping.Dispose();
        }
    }
}
