using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Debugging;

/// <summary>現在の参加集合へ一時予約を配り、一つの所有実体で次の要求を一度だけ選びます。</summary>
public sealed class WorkflowDebugService
{
    private readonly PeerCluster cluster;
    private readonly TimeProvider clock;
    private long armOrder;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Control> controls = [];
    private static string PrepareKind => "workflow.debug.prepare";
    private static string ClaimKind => "workflow.debug.claim";
    private static string CancelKind => "workflow.debug.cancel";
    private static string ReadKind => "workflow.debug.snapshot";

    /// <summary>永続履歴を作らない診断専用の内部要求だけを登録します。</summary>
    public WorkflowDebugService(PeerCluster cluster, WorkflowDebugRecorder recorder, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(recorder);
        this.cluster = cluster;
        clock = timeProvider ?? TimeProvider.System;
        recorder.Claim = ClaimAsync;
        cluster.RegisterRuntimeHandler(PrepareKind, (request, _) =>
        {
            WorkflowDebugArm arm = request.Payload.Json.Deserialize<WorkflowDebugArm>()!;

            // 旧Coreからの予約には換算済みの期限がありません。仮保持を10分に限り、実際の取得権は必ず元の所有者に照会します。
            TimeSpan remaining = request.Admission?.ForDeadline(arm.ExpiresAt).Remaining ?? WorkflowDebugArm.Lifetime;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(recorder.Prepare(arm, remaining))));
        });
        cluster.RegisterRuntimeHandler(ClaimKind, (request, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded,
            NodeValue.FromObject(Claim(request.Payload.Json.Deserialize<ClaimRequest>()!)))));
        cluster.RegisterRuntimeHandler(CancelKind, (request, _) =>
        {
            recorder.Cancel(request.Payload.Json.GetGuid());
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        });
        cluster.RegisterInspection(ReadKind, (request, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded,
            NodeValue.FromObject(recorder.Read(request.Payload.Json.Deserialize<WorkflowDebugQuery>()!)))));
    }

    /// <summary>同じ操作の再送では記録を再予約せず、固定参加集合の確認を待ちます。</summary>
    /// <returns>予約済み・取得中・準備未確認の状態です。</returns>
    public async Task<WorkflowDebugView> ArmAsync(WorkflowDebugRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("A debug capture operation ID is required.", nameof(request));
        }
        _ = await LatestAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);
        Control control;
        bool created = false;
        using (gate.EnterScope())
        {
            Prune();
            if (!controls.TryGetValue(request.OperationId, out control!))
            {
                if (16 <= controls.Count || controls.Values.Any(item => string.Equals(item.Arm.WorkflowId, request.WorkflowId, StringComparison.Ordinal) && item.State is EWorkflowDebugState.Preparing or EWorkflowDebugState.Armed or EWorkflowDebugState.Recording))
                {
                    throw new InvalidOperationException("A capture is already active, or the temporary capture limit was reached.");
                }
                PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
                if (members.Length == 0 || cluster.IsDetached)
                {
                    throw new InvalidOperationException("Debug capture requires a participating cluster.");
                }
                RequestAdmission reservation = RequestAdmission.Start(clock, WorkflowDebugArm.Lifetime);
                control = new(new(request.OperationId, request.WorkflowId, cluster.LocalPeerId, cluster.LocalInstanceId, reservation.ExpiresAt), members, reservation, checked(++armOrder));
                controls.Add(request.OperationId, control);
                created = true;
            }
            if (!string.Equals(control.Arm.WorkflowId, request.WorkflowId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The capture operation belongs to another workflow.");
            }
        }
        if (created)
        {
            try
            {
                foreach (PeerInfo member in control.Members)
                {
                    ClusterResponse response = await PrepareAsync(control, member, cancellationToken).ConfigureAwait(false);
                    if (response.Outcome != EOutcome.Succeeded || response.Value?.Json.ValueKind != JsonValueKind.True)
                    {
                        throw new IOException("Debug preparation: " + member.PeerId + " / " + response.Outcome + " / " + response.Reason);
                    }
                }
                using (gate.EnterScope())
                {
                    if (control.State == EWorkflowDebugState.Preparing)
                    {
                        control.State = EWorkflowDebugState.Armed;
                    }
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                using (gate.EnterScope())
                {
                    control.State = EWorkflowDebugState.Unconfirmed;
                    control.Reason = "debug.preparationUnconfirmed: " + error.Message;
                }
            }
        }
        WorkflowDebugView result = await ReadAsync(new(request.OperationId), cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Debug capture expired.");
        return result with { Nodes = null, NextOffset = null };
    }

    /// <summary>ページ再表示でも直近の記録を見つけます。予約は再開しません。</summary>
    /// <returns>この接続先が予約した最新の記録です。</returns>
    public Task<WorkflowDebugView?> LatestAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        Guid? id;
        using (gate.EnterScope())
        {
            Prune();
            id = controls.Values.Where(item => string.Equals(item.Arm.WorkflowId, workflowId, StringComparison.Ordinal)).MaxBy(item => item.Order)?.Arm.Id;
        }
        if (id is Guid found)
        {
            return ReadAsync(new(found), cancellationToken);
        }
        return Task.FromResult<WorkflowDebugView?>(null);
    }

    /// <summary>取得した一件の所有実体だけを照会し、別実体の履歴へ置き換えません。</summary>
    /// <returns>有限ページまたは取得不能の状態です。</returns>
    public async Task<WorkflowDebugView?> ReadAsync(WorkflowDebugQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        WorkflowDebugView view;
        using (gate.EnterScope())
        {
            Prune();
            if (!controls.TryGetValue(query.Id, out Control? control))
            {
                return null;
            }
            if (control.State is EWorkflowDebugState.Armed && (control.Reservation.IsExpired || !Matches(control)))
            {
                control.State = EWorkflowDebugState.Expired;
                control.Reason = "debug.expiredOrMembershipChanged";
            }
            view = new(control.Arm, control.State, control.Execution, CapturePeerId: control.Peer, CaptureInstanceId: control.Instance, Reason: control.Reason);
        }
        if (view.CapturePeerId is null || view.CaptureInstanceId is not Guid instance)
        {
            return view;
        }
        try
        {
            ClusterResponse response = await SendAsync(ReadKind, view.CapturePeerId, instance, NodeValue.FromObject(query), true, cancellationToken).ConfigureAwait(false);
            WorkflowDebugView? captured = response.Value?.Json.Deserialize<WorkflowDebugView>();
            if (response.Outcome == EOutcome.Succeeded && captured is not null)
            {
                using (gate.EnterScope())
                {
                    if (controls.TryGetValue(query.Id, out Control? control))
                    {
                        control.State = captured.State;
                    }
                }
                return captured;
            }
            return view with { Reason = "debug.resultUnavailable" };
        }
        catch (IOException)
        {
            return view with { Reason = "debug.ownerUnavailable" };
        }
    }

    /// <summary>診断だけを停止し、業務の実行を取り消しません。</summary>
    /// <returns>取得済みの記録を含む停止状態です。</returns>
    public async Task<WorkflowDebugView?> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Control? control;
        using (gate.EnterScope())
        {
            if (!controls.TryGetValue(id, out control))
            {
                return null;
            }
            control.State = EWorkflowDebugState.Cancelled;
        }
        bool confirmed = true;
        foreach (PeerInfo member in control.Members)
        {
            try
            {
                ClusterResponse response = await SendAsync(CancelKind, member.PeerId, member.InstanceId, NodeValue.FromObject(id), false, cancellationToken).ConfigureAwait(false);
                confirmed &= response.Outcome == EOutcome.Succeeded;
            }
            catch (IOException)
            {
                // 他の参加Coreにも停止を届けた後で、未確認を呼出元へ返します。
                confirmed = false;
            }
        }
        if (!confirmed)
        {
            throw new IOException("Debug cancellation was not confirmed by every participating peer.");
        }
        WorkflowDebugView? result = await ReadAsync(new(id), cancellationToken).ConfigureAwait(false);
        return result is null ? null
                              : result with { Nodes = null, NextOffset = null };
    }

    /// <summary>準備中は予約を消費せず、準備後の先着一件だけを所有者の排他で採用します。</summary>
    /// <returns>準備中はnull、採用はtrue、他の要求で取得済みならfalseです。</returns>
    private bool? Claim(ClaimRequest request)
    {
        using (gate.EnterScope())
        {
            if (!controls.TryGetValue(request.Id, out Control? control) || control.Reservation.IsExpired || !Matches(control))
            {
                return false;
            }
            if (control.State is EWorkflowDebugState.Cancelled or EWorkflowDebugState.Expired or EWorkflowDebugState.Unconfirmed)
            {
                return false;
            }
            if (control.State is EWorkflowDebugState.Preparing)
            {
                return null;
            }
            if (control.State is not EWorkflowDebugState.Armed || !control.Members.Any(peer => string.Equals(peer.PeerId, request.Peer, StringComparison.Ordinal) && peer.InstanceId == request.Instance))
            {
                return control.Execution == request.Execution && string.Equals(control.Peer, request.Peer, StringComparison.Ordinal) && control.Instance == request.Instance;
            }
            control.Execution = request.Execution;
            control.Peer = request.Peer;
            control.Instance = request.Instance;
            control.State = EWorkflowDebugState.Recording;
            return true;
        }
    }

    /// <summary>無効時は呼ばれない短い一回分の取得要求です。</summary>
    /// <returns>固定した予約所有者が確認した採否です。</returns>
    private async ValueTask<bool?> ClaimAsync(WorkflowDebugArm arm, Guid execution, CancellationToken cancellationToken = default)
    {
        ClaimRequest body = new(arm.Id, execution, cluster.LocalPeerId, cluster.LocalInstanceId);
        ClusterResponse result = await SendAsync(ClaimKind, arm.OwnerPeerId, arm.OwnerInstanceId, NodeValue.FromObject(body), false, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != EOutcome.Succeeded)
        {
            throw new IOException("The debug capture owner did not confirm admission.");
        }
        return result.Value?.Json.Deserialize<bool?>();
    }

    /// <summary>参加集合が変わった予約で、次の入力を推測して選び直しません。</summary>
    /// <returns>予約時の実体集合と一致するかです。</returns>
    private bool Matches(Control control) => control.Members.Select(peer => peer.InstanceId).ToHashSet()
        .SetEquals(cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).Select(peer => peer.InstanceId));

    /// <summary>受付要求と予約期限を同じ発行元の時計で表し、配布までに経過した時間を予約から差し引きます。</summary>
    /// <returns>固定した参加実体での準備結果です。</returns>
    private Task<ClusterResponse> PrepareAsync(Control control, PeerInfo member, CancellationToken cancellationToken = default)
    {
        TimeSpan remaining = control.Reservation.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            return Task.FromResult(new ClusterResponse(EOutcome.NotExecuted, Reason: "debug.expired"));
        }
        DateTimeOffset now = control.Arm.ExpiresAt - remaining;
        TimeSpan delivery = TimeSpan.FromSeconds(15);
        if (remaining < delivery)
        {
            delivery = remaining;
        }
        RequestAdmission admission = new(clock, delivery, now + delivery, now);
        ClusterRequest request = new(Guid.CreateVersion7(now), PrepareKind, member.PeerId, member.InstanceId, admission.ExpiresAt, NodeValue.FromObject(control.Arm)) { Admission = admission };
        return cluster.RequestAsync(request, cancellationToken);
    }

    /// <summary>内部操作と読取りを既存の固定実体通信へ分けます。</summary>
    /// <returns>配送確認または取得不能です。</returns>
    private Task<ClusterResponse> SendAsync(string kind, string peer, Guid instance, NodeValue value, bool read, CancellationToken cancellationToken = default)
    {
        ClusterRequest request = ClusterRequest.Start(kind, peer, instance, TimeSpan.FromSeconds(15), value);
        return read ? cluster.InspectAsync(request, cancellationToken)
                    : cluster.RequestAsync(request, cancellationToken);
    }

    /// <summary>記録は再起動を跨がず、予約から30分後にはメタデータも削除します。</summary>
    private void Prune()
    {
        foreach (Guid id in controls.Where(pair => pair.Value.Reservation.Remaining <= -WorkflowDebugArm.Retention).Select(pair => pair.Key).ToArray())
        {
            controls.Remove(id);
        }
    }

    /// <summary>取得権の競合だけを短い排他で直列化します。</summary>
    private sealed record Control(WorkflowDebugArm Arm, PeerInfo[] Members, RequestAdmission Reservation, long Order)
    {
        internal EWorkflowDebugState State
        {
            get; set;
        } = EWorkflowDebugState.Preparing;

        internal string? Reason
        {
            get; set;
        }

        internal Guid? Execution
        {
            get; set;
        }

        internal string? Peer
        {
            get; set;
        }

        internal Guid? Instance
        {
            get; set;
        }
    }

    /// <summary>Sourceの実行所有者を変えず、診断の取得権だけを問い合わせます。</summary>
    private sealed record ClaimRequest(Guid Id, Guid Execution, string Peer, Guid Instance);
}
