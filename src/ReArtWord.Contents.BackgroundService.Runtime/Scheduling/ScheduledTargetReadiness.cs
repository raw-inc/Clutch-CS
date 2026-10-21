using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>発生時に不在だった実行先だけを待ち、開始済み処理には遅延方針を遡及しません。</summary>
internal sealed class ScheduledTargetReadiness(IPeerDirectory peers, TimeProvider clock, TimeSpan admissionLifetime)
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, ScheduledInput> inputs = [];
    private readonly Dictionary<TargetKey, PendingTarget> targets = [];

    /// <summary>その入力がローカルの待機キューまたは実行中処理に保持されているかを確認します。</summary>
    /// <returns>ローカルで追跡中か。</returns>
    internal bool Contains(Guid id)
    {
        using (gate.EnterScope())
        {
            return inputs.ContainsKey(id);
        }
    }

    /// <summary>待機キューへ入った時点で最新予定を確認し、実行枠が満杯でも古い対象待ちを解消できるようにします。</summary>
    /// <returns>保持上限内で入力を記録できたか。</returns>
    internal bool Track(NodeEmission emission)
    {
        ScheduledOccurrence? schedule = emission.Context.Schedule;
        if (schedule is null || emission.IsTerminal)
        {
            return true;
        }
        IReadOnlyDictionary<string, TargetResolution>? captured = emission.Context.CapturedTargets;
        if (captured is null)
        {
            return false;
        }
        using (gate.EnterScope())
        {
            if (inputs.ContainsKey(emission.Context.CausationId))
            {
                return true;
            }
            List<TargetKey> keys = [];
            foreach (KeyValuePair<string, TargetResolution> node in captured)
            {
                foreach (PeerInfo peer in node.Value.Peers)
                {
                    TargetKey key = new(emission.Context.WorkflowId, schedule.TimelineKey, node.Key, peer.PeerId);
                    if (!Available(peer) || targets.ContainsKey(key))
                    {
                        keys.Add(key);
                    }
                }
            }
            if (4096 <= inputs.Count || 65536 < targets.Count + keys.Count)
            {
                return false;
            }
            foreach (TargetKey key in keys)
            {
                if (!targets.TryGetValue(key, out PendingTarget? target))
                {
                    target = new(schedule.ScheduledAt);
                    targets.Add(key, target);
                }
                target.Users++;
                if (target.Latest < schedule.ScheduledAt)
                {
                    target.Latest = schedule.ScheduledAt;
                }
            }
            inputs.Add(emission.Context.CausationId, new(schedule, captured, keys));
            return true;
        }
    }

    /// <summary>最後の関連入力が終了するまで最新時刻を保持し、遅い古い入力を復活させません。</summary>
    internal void Release(Guid id)
    {
        using (gate.EnterScope())
        {
            if (!inputs.Remove(id, out ScheduledInput? input))
            {
                return;
            }
            foreach (TargetKey key in input.Keys)
            {
                PendingTarget target = targets[key];
                target.Users--;
                if (target.Users == 0)
                {
                    targets.Remove(key);
                }
            }
        }
    }

    /// <summary>未配送の対象だけを待機し、元の入力の取消と最新回への置換を確認します。</summary>
    /// <returns>実行実体を固定した要求、または未開始の理由。</returns>
    internal async Task<ScheduledTargetPreparation> PrepareAsync(RemoteExecutionRequest request, Func<bool> cancelled, CancellationToken cancellationToken = default)
    {
        ScheduledInput? input;
        using (gate.EnterScope())
        {
            input = inputs.GetValueOrDefault(request.CausationId);
        }
        PeerInfo? original = input?.Captured.GetValueOrDefault(request.Node.Id)?.Peers.FirstOrDefault(peer => string.Equals(peer.PeerId, request.TargetPeerId, StringComparison.Ordinal));
        if (input is null || original is null || Available(original))
        {
            return new(request);
        }
        ScheduledOccurrence schedule = input.Schedule;
        TargetKey key = new(request.WorkflowId, schedule.TimelineKey, request.Node.Id, request.TargetPeerId);
        while (!cancelled() && !cancellationToken.IsCancellationRequested)
        {
            using (gate.EnterScope())
            {
                if (targets.TryGetValue(key, out PendingTarget? target) && schedule.ScheduledAt < target.Latest)
                {
                    return Reject(request, "schedule.superseded", "同じ実行先の新しい予定回を保持し、未開始の古い回を見送りました。");
                }
            }
            DateTimeOffset now = clock.GetUtcNow();
            if (schedule.MissedPolicy == EScheduleMissedPolicy.Skip
                || schedule.MissedPolicy == EScheduleMissedPolicy.Within && schedule.ScheduledAt + schedule.Tolerance < now)
            {
                return Reject(request, "schedule.targetMissed", "予定時刻の不在、または許容遅延超過により、この実行先の回を見送りました。");
            }
            PeerInfo? current = peers.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, original.PeerId, StringComparison.Ordinal) && Available(peer));
            if (current is not null)
            {
                if (!string.IsNullOrEmpty(original.HostId) && !string.Equals(original.HostId, current.HostId, StringComparison.Ordinal))
                {
                    return Reject(request, "schedule.hostChanged", "予定の実行先とは異なるPCになったため、別PCでは開始しません。");
                }
                return new(request with { TargetInstanceId = current.InstanceId, ExpiresAt = now + admissionLifetime });
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Reject(request, "schedule.cancelled", "待機中の予定入力を取り消しました。", EOutcome.Cancelled);
            }
        }
        return Reject(request, "schedule.cancelled", "元の予定入力の取消により、待機中の処理を開始しません。", EOutcome.Cancelled);
    }

    /// <summary>通常参加と、明示離脱中に許可された自PCを区別します。</summary>
    /// <returns>そのPCで処理を開始できるか。</returns>
    private bool Available(PeerInfo peer) => PeerStatusRules.CanParticipate(peer.Status) || peer.InstanceId == peers.LocalInstanceId && peer.Status == EPeerStatus.Detached;

    /// <summary>配送を行わなかった理由を対象別結果へ残します。</summary>
    /// <returns>元の要求と未開始結果。</returns>
    private static ScheduledTargetPreparation Reject(RemoteExecutionRequest request, string code, string message, EOutcome outcome = EOutcome.NotExecuted)
        => new(request, NodeResult.Failure(code, message, outcome));

    /// <summary>対象待ちを共有する時間指定・ノード・PCの組合せ。</summary>
    /// <param name="Workflow">ワークフロー。</param>
    /// <param name="Timeline">時間指定の実行区分。</param>
    /// <param name="Node">処理ノード。</param>
    /// <param name="Peer">固定した実行先。</param>
    private sealed record TargetKey(string Workflow, string Timeline, string Node, string Peer);

    /// <summary>一件が保持した対象と、参照中の待機先。</summary>
    /// <param name="Schedule">発生時の予定。</param>
    /// <param name="Captured">発生時の対象。</param>
    /// <param name="Keys">参照を解放する待機先。</param>
    private sealed record ScheduledInput(ScheduledOccurrence Schedule, IReadOnlyDictionary<string, TargetResolution> Captured, IReadOnlyList<TargetKey> Keys);

    /// <summary>関連する入力が残っている間、最新予定を維持します。</summary>
    private sealed record PendingTarget
    {
        /// <summary>この対象を初めて保持した予定時刻を指定します。</summary>
        internal PendingTarget(DateTimeOffset latest) => Latest = latest;

        /// <summary>確認した最新の予定時刻。</summary>
        internal DateTimeOffset Latest
        {
            get; set;
        }

        /// <summary>この対象を保持している入力数。</summary>
        internal int Users
        {
            get; set;
        }
    }
}
