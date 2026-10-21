using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>未確認の相手ごとに再同期を要求し、古い完了で新しい失敗を消しません。</summary>
internal sealed class SyncRecovery(TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Failure> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> confirmed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Guid Instance, long Started)> changing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Guid Instance, DateTimeOffset RetryAt)> preparing = new(StringComparer.Ordinal);
    private long revision;

    /// <summary>全相手のうち最後に確認できなかった理由です。</summary>
    internal string? Problem
    {
        get
        {
            using (gate.EnterScope())
            {
                Failure? latest = null;
                foreach (Failure failure in failures.Values)
                {
                    if (latest is null || latest.Revision < failure.Revision)
                    {
                        latest = failure;
                    }
                }
                return latest?.Reason;
            }
        }
    }

    /// <summary>同期開始時点までの失敗だけを完了確認の対象にします。</summary>
    internal long Revision
    {
        get
        {
            using (gate.EnterScope())
            {
                return revision;
            }
        }
    }

    /// <summary>共有版が等しくても、同じ実体との初回確認や未確認障害が残っていれば同期します。</summary>
    /// <returns>競合の明示解消以外に再同期が必要かです。</returns>
    internal bool NeedsSynchronization(PeerInfo peer, bool conflictsResolved = false)
    {
        using (gate.EnterScope())
        {
            if (failures.TryGetValue(peer.PeerId, out Failure? failure))
            {
                return failure.Instance != peer.InstanceId || conflictsResolved || !string.Equals(failure.Reason, "Sync.Conflict", StringComparison.Ordinal);
            }
            return !confirmed.TryGetValue(peer.PeerId, out Guid instance) || instance != peer.InstanceId;
        }
    }

    /// <summary>失敗直後の全書出し連打を避け、相手の新実体は直ちに確認します。</summary>
    /// <returns>次の有限同期を開始できるかです。</returns>
    internal bool CanSynchronize(PeerInfo peer)
    {
        using (gate.EnterScope())
        {
            if (preparing.TryGetValue(peer.PeerId, out (Guid Instance, DateTimeOffset RetryAt) pending) && pending.Instance == peer.InstanceId && clock.GetUtcNow() < pending.RetryAt)
            {
                return false;
            }
            return !failures.TryGetValue(peer.PeerId, out Failure? failure) || failure.Instance != peer.InstanceId || failure.RetryAt <= clock.GetUtcNow();
        }
    }

    /// <summary>書込み途中の比較だけを最長5秒保留し、連続変更が実際の分岐を隠し続けないようにします。</summary>
    /// <returns>次の通知で再照合できる猶予が残っているかです。</returns>
    internal bool DeferChangingVersion(PeerInfo peer, bool observedChange)
    {
        using (gate.EnterScope())
        {
            if (!observedChange)
            {
                changing.Remove(peer.PeerId);
                return false;
            }
            if (!changing.TryGetValue(peer.PeerId, out (Guid Instance, long Started) previous) || previous.Instance != peer.InstanceId)
            {
                previous = (peer.InstanceId, clock.GetTimestamp());
                changing[peer.PeerId] = previous;
            }
            return clock.GetElapsedTime(previous.Started) < TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>再試行間隔を1秒から30秒まで増やし、成功するまで理由を保持します。</summary>
    internal void Failed(PeerInfo peer, string reason)
    {
        string pendingReason = "Sync.PendingOperations";
        if (string.Equals(reason, pendingReason, StringComparison.Ordinal))
        {
            // 通常コミット中の準備を転送失敗として残すと、版一致後も全書出しを繰り返します。
            // 元の未確認障害は消さず、本文の再書出し前に短い再照合間隔を置きます。
            using (gate.EnterScope())
            {
                preparing[peer.PeerId] = (peer.InstanceId, clock.GetUtcNow().AddSeconds(1));
            }
            return;
        }
        using (gate.EnterScope())
        {
            int delay = 1;
            if (failures.TryGetValue(peer.PeerId, out Failure? previous) && previous.Instance == peer.InstanceId)
            {
                delay = Math.Min(30, previous.DelaySeconds * 2);
            }
            failures[peer.PeerId] = new(peer.InstanceId, reason, checked(++revision), clock.GetUtcNow().AddSeconds(delay), delay);
        }
    }

    /// <summary>検証済みの同じ相手の同期で、開始前の失敗だけを解除します。</summary>
    internal void Confirmed(string peerId, Guid instanceId, long startedRevision)
    {
        using (gate.EnterScope())
        {
            confirmed[peerId] = instanceId;
            if (changing.TryGetValue(peerId, out (Guid Instance, long Started) change) && change.Instance == instanceId)
            {
                changing.Remove(peerId);
            }
            if (preparing.TryGetValue(peerId, out (Guid Instance, DateTimeOffset RetryAt) pending) && pending.Instance == instanceId)
            {
                preparing.Remove(peerId);
            }
            if (failures.TryGetValue(peerId, out Failure? failure) && failure.Revision <= startedRevision)
            {
                failures.Remove(peerId);
            }
        }
    }

    /// <summary>相手の最新の未確認結果と有限の再試行間隔です。</summary>
    private sealed record Failure(Guid Instance, string Reason, long Revision, DateTimeOffset RetryAt, int DelaySeconds);
}
