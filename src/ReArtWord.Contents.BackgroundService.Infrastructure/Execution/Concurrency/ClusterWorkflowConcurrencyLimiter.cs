using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;

/// <summary>区間の所有は元の実行に置き、取得・解放をクラスタの現在の担当へ確認します。</summary>
public sealed class ClusterWorkflowConcurrencyLimiter : IWorkflowConcurrencyLimiter, IAsyncDisposable
{
    private readonly PeerCluster cluster;
    private readonly ConcurrencyAuthority authority;
    private readonly ConcurrencyOwnership owned;
    private static string AcquireKind => "workflow.limit.acquire";
    private static string ReleaseKind => "workflow.limit.release";
    private static string SnapshotKind => "workflow.limit.snapshot";

    /// <summary>受付、所有確認、現在表示をそれぞれの通信境界へ登録します。</summary>
    public ClusterWorkflowConcurrencyLimiter(PeerCluster cluster, IDurableRepository repository, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.cluster = cluster;
        owned = new(cluster);
        authority = new(cluster, repository, owned.ReadAsync, timeProvider ?? TimeProvider.System);
        cluster.RegisterDeferredRuntimeHandler(AcquireKind, authority.AcquireAsync);
        cluster.RegisterRuntimeHandler(ReleaseKind, authority.ReleaseAsync);
        cluster.RegisterInspection(SnapshotKind, authority.ReadAsync);
        cluster.EnableBufferedRuntime();
    }

    /// <summary>待機・取得中は同じ到達番号を保持し、不明応答から後続を開始しません。</summary>
    /// <returns>元の区間が解放する枠、または取得を確認できなかった理由です。</returns>
    // 抑制理由: 取得したLeaseの所有権を受付結果へ移し、区間実行器がawait usingで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Leaseを受付結果として移譲し、区間と子処理の終了まで呼出元が所有します。")]
    public async Task<WorkflowConcurrencyAdmission> AcquireAsync(WorkflowConcurrencyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!owned.TryAdd(request))
        {
            return new(null, NodeResult.Failure("concurrency.owner", "The execution owner or admission capacity is invalid."));
        }
        bool acquired = false;
        try
        {
            PeerInfo? coordinator = Coordinator();
            if (coordinator is null)
            {
                return new(null, NodeResult.Failure("concurrency.unavailable", "Cluster concurrency coordination is unavailable."));
            }
            ClusterRequest command = new(Guid.CreateVersion7(), AcquireKind, coordinator.PeerId, coordinator.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(request));
            ClusterResponse response = await cluster.RequestAsync(command, cancellationToken).ConfigureAwait(false);
            response = await RemoteNodeCompletion.ConfirmAsync(cluster, command, response, cancellationToken).ConfigureAwait(false);
            if (response.Outcome != EOutcome.Succeeded || response.Value is null)
            {
                string message = response.Reason switch
                {
                    "concurrency.timeout" => "The queue timeout elapsed before a concurrency slot became available.",
                    _ => "The concurrency permit was not confirmed; downstream work was not started.",
                };
                return new(null, NodeResult.Failure(response.Reason ?? "concurrency.unconfirmed", message, response.Outcome));
            }
            ConcurrencyPermit? permit = response.Value.Json.Deserialize<ConcurrencyPermit>();
            if (permit?.Request != request)
            {
                return new(null, NodeResult.Failure("concurrency.mismatchedPermit", "The returned permit does not belong to this execution.", EOutcome.Unknown));
            }
            acquired = true;
            return new(new PermitLease(this, request), Forced: permit.Forced);
        }
        finally
        {
            if (!acquired)
            {
                await ReleaseOwnedAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>表示対象だけをまとめて照会し、取得不能を空キューと区別します。</summary>
    /// <returns>現在の占有数と有限件数の待機情報です。</returns>
    // 抑制理由: 最大100ノードの同じ照会結果を一括表示し、取得途中の部分結果を現在値と混在させません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW160", Justification = "上限付きの一括スナップショットをAPI応答として返します。")]
    public async Task<IReadOnlyList<WorkflowConcurrencySnapshot>> ReadAsync(IReadOnlyList<WorkflowConcurrencyTarget> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targets.Count, 100);
        if (targets.Count == 0)
        {
            return [];
        }
        PeerInfo? coordinator = Coordinator();
        if (coordinator is not null)
        {
            ClusterRequest query = new(Guid.NewGuid(), SnapshotKind, coordinator.PeerId, coordinator.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(3), NodeValue.FromObject(targets));
            ClusterResponse result = await cluster.InspectAsync(query, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == EOutcome.Succeeded && result.Value?.Json.Deserialize<WorkflowConcurrencySnapshot[]>() is { } snapshots)
            {
                return snapshots;
            }
        }
        return targets.Select(target => new WorkflowConcurrencySnapshot(target.WorkflowId, target.NodeId, 0, 0, [], DateTimeOffset.UtcNow, "concurrency.unavailable")).ToArray();
    }

    /// <summary>取得担当の欠測時に別Peerへ分散した枠を作りません。</summary>
    /// <returns>現在の通常参加中の担当、またはnullです。</returns>
    private PeerInfo? Coordinator()
    {
        if (cluster.IsDetached)
        {
            return null;
        }
        return cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status));
    }

    /// <summary>ローカル所有を確実に閉じ、解放の未確認分は次の所有照会で回収可能にします。</summary>
    /// <returns>有限時間の解放通知です。</returns>
    private async Task ReleaseOwnedAsync(WorkflowConcurrencyRequest request, CancellationToken cancellationToken = default)
    {
        owned.Remove(request.TicketId);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            PeerInfo? coordinator = Coordinator();
            if (coordinator is not null)
            {
                ClusterRequest command = new(Guid.CreateVersion7(), ReleaseKind, coordinator.PeerId, coordinator.InstanceId,
                    DateTimeOffset.UtcNow.AddSeconds(5), NodeValue.FromObject(request));
                _ = await cluster.RequestAsync(command, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // 一時欠測で終了した区間を復活させず、保存された枠は担当による所有確認まで維持します。
        }
    }

    /// <summary>実行が終了した後に担当側の資源を解放します。</summary>
    /// <returns>担当に残る待機の回収です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名で、全参照の終了後に内部資源を解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名で、担当の終了待ちをそのまま返します。")]
    public ValueTask DisposeAsync() => authority.DisposeAsync();

    /// <summary>一回だけ解放する実行区間の所有権です。</summary>
    private sealed class PermitLease(ClusterWorkflowConcurrencyLimiter owner, WorkflowConcurrencyRequest request) : IAsyncDisposable
    {
        private int released;

        /// <summary>重複した後始末でも同じ到達を二度処理しません。</summary>
        /// <returns>元の所有者への解放通知です。</returns>
        // 抑制理由: IAsyncDisposableの固定署名であり、通知側が有限の期限を所有します。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名で、元の所有者が解放通知の期限を管理します。")]
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }
            return new(owner.ReleaseOwnedAsync(request));
        }
    }
}
