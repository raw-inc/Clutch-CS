using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>PCとクラスタの排他を守って有限並列で更新し、進捗を一列に保存します。</summary>
internal sealed class FleetUpdateRunner(IFleetUpdateRemote remote)
{
    /// <summary>安全な対象を空き枠へ割り当て、結果不明後は開始済み処理だけを回収します。</summary>
    /// <returns>保存した全対象の結果です。</returns>
    // 抑制理由: 所有する全taskをfinallyで回収してから保存排他を解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "全taskをfinallyでawaitし、借用資源の寿命内に回収します。")]
    internal async Task<FleetUpdateStatus> RunAsync(FleetUpdateStatus initial, Func<FleetUpdateStatus, CancellationToken, Task> save, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initial.Request.Release.MaximumParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initial.Request.Release.MaximumParallelism, 3);
        FleetUpdateStatus status = initial;
        FleetPeerUpdater worker = new(remote);
        using SemaphoreSlim journal = new(1, 1);
        List<int> pending = Enumerable.Range(0, initial.Peers.Count).ToList();
        Dictionary<int, Task> running = [];
        bool halted = false;
        string preparing = "Preparing", updating = "Updating", unconfirmed = "Unconfirmed";
        Exception? storageFailure = null;
        try
        {
            while (pending.Count != 0 || running.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (int index in pending.ToArray())
                {
                    if (Volatile.Read(ref halted) || initial.Request.Release.MaximumParallelism <= running.Count)
                    {
                        break;
                    }
                    FleetUpdateTarget target = initial.Peers[index].Target;
                    if (running.Keys.Any(active => Conflicts(target, initial.Peers[active].Target)))
                    {
                        continue;
                    }
                    pending.Remove(index);
                    running.Add(index, RunPeerAsync(index));
                }
                if (running.Count == 0)
                {
                    break;
                }
                await Task.WhenAny(running.Values).ConfigureAwait(false);
                foreach (int index in running.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray())
                {
                    await running[index].ConfigureAwait(false);
                    running.Remove(index);
                }
            }
        }
        finally
        {
            Volatile.Write(ref halted, true);
            await Task.WhenAll(running.Values).ConfigureAwait(false);
        }
        bool unknown = status.Peers.Any(peer => peer.State is "Unconfirmed");
        bool success = status.Peers.All(peer => peer.State is "Completed");
        EOutcome outcome = EOutcome.Failed;
        if (success)
        {
            outcome = EOutcome.Succeeded;
        }
        if (unknown)
        {
            outcome = EOutcome.Unknown;
        }
        status = status with { Completed = true, Outcome = outcome,
            Reason = status.Peers.FirstOrDefault(peer => peer.State is "Unconfirmed")?.Reason, UpdatedAt = DateTimeOffset.UtcNow };
        await save(status, cancellationToken).ConfigureAwait(false);
        return status;

        async Task RunPeerAsync(int index)
        {
            FleetUpdatePeerProgress peer = initial.Peers[index];
            string? reason = await worker.RemainingAsync(initial.Request, peer.Target, cancellationToken).ConfigureAwait(false);
            FleetUpdatePeerProgress result;
            if (reason is not null)
            {
                result = peer with { State = "Deferred", Reason = reason };
            }
            else
            {
                result = await worker.UpdateAsync(initial.Request, peer, progress => RecordAsync(index, progress), cancellationToken).ConfigureAwait(false);
            }
            await RecordAsync(index, result).ConfigureAwait(false);
        }

        // 抑制理由: 保存障害で新規受付を止め、元の例外を呼出元へ維持します。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "停止フラグを確定してから元の保存障害を伝播します。")]
        async Task<bool> RecordAsync(int index, FleetUpdatePeerProgress progress)
        {
            await journal.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (storageFailure is not null)
                {
                    throw new IOException("The update journal is unavailable.", storageFailure);
                }
                if (Volatile.Read(ref halted) && (string.Equals(progress.State, preparing, StringComparison.Ordinal) || string.Equals(progress.State, updating, StringComparison.Ordinal)))
                {
                    return false;
                }
                FleetUpdatePeerProgress[] peers = status.Peers.ToArray();
                peers[index] = progress;
                FleetUpdateStatus next = status with { Peers = peers, UpdatedAt = DateTimeOffset.UtcNow };
                if (string.Equals(progress.State, unconfirmed, StringComparison.Ordinal))
                {
                    Volatile.Write(ref halted, true);
                }
                try
                {
                    await save(next, cancellationToken).ConfigureAwait(false);
                    status = next;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
                {
                    storageFailure = exception;
                    Volatile.Write(ref halted, true);
                    throw;
                }
                return true;
            }
            finally
            {
                journal.Release();
            }
        }
    }

    /// <summary>同じPCまたはクラスタに対する更新は復帰確認まで重ねません。</summary>
    /// <returns>同時更新できない組合せかです。</returns>
    private static bool Conflicts(FleetUpdateTarget first, FleetUpdateTarget second)
        => string.Equals(first.Peer.HostId, second.Peer.HostId, StringComparison.Ordinal)
            || string.Equals(first.ClusterId, second.ClusterId, StringComparison.Ordinal);
}
