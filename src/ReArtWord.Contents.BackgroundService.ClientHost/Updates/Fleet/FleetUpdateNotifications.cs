using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>永続進捗の観測結果から、秘密や配置パスを含まないHook通知を作成します。</summary>
internal static class FleetUpdateNotifications
{
    /// <summary>クラスタごとに全体結果と、そのクラスタの対象別結果を発行します。</summary>
    /// <returns>同じ内容では同じIDになる通知です。</returns>
    internal static IReadOnlyList<UpdateNotification> Capture(FleetUpdateStatus status, string clusterId) => Capture(FleetUpdateView.From(status), clusterId);

    /// <summary>表示用進捗からも同じIDのHook通知を生成します。</summary>
    /// <returns>マニフェストの再読込みを必要としない通知です。</returns>
    internal static IReadOnlyList<UpdateNotification> Capture(FleetUpdateView status, string clusterId)
    {
        Guid operation = status.OperationId;
        string release = status.ReleaseId;
        UpdateEventData summary = new(operation, EOutcome.Unknown, false, ReleaseId: release, ClusterId: clusterId, Stage: "fleet");
        List<UpdateNotification> notifications = [UpdateNotification.Create("update.started", summary, status.UpdatedAt)];
        foreach (FleetUpdatePeerProgress peer in status.Peers.Where(item => string.Equals(item.Target.ClusterId, clusterId, StringComparison.Ordinal)))
        {
            foreach (FleetUpdateAttempt attempt in peer.Attempts)
            {
                UpdatePeerResult? result = attempt.Result;
                bool completed = result?.Phase is EUpdatePhase.Completed or EUpdatePhase.RolledBack or EUpdatePhase.Failed or EUpdatePhase.Interrupted;
                UpdateEventData data = new(operation, result?.Outcome ?? EOutcome.Unknown, completed, peer.Target.Peer.PeerId,
                    result?.Phase, result?.PreparedInstanceId ?? attempt.PreviousInstanceId, result?.InstanceId, result?.ReleaseId, result?.Reason,
                    attempt.OperationId, clusterId, "peer." + (result?.Phase.ToString() ?? "unconfirmed"));
                notifications.Add(UpdateNotification.Create("update.peerChanged", data, status.UpdatedAt));
            }
        }
        if (status.Client is { } client)
        {
            UpdateEventData data = new(operation, client.Outcome, client.Completed, ReleaseId: release, ClusterId: clusterId, Stage: "clienthost." + client.State);
            notifications.Add(UpdateNotification.Create("update.peerChanged", data, client.UpdatedAt));
        }
        if (status.Completed)
        {
            summary = summary with { Outcome = status.Outcome, Completed = true };
            notifications.Add(UpdateNotification.Create("update.completed", summary, status.UpdatedAt));
            if (status.Outcome == EOutcome.Unknown)
            {
                notifications.Add(UpdateNotification.Create("update.interrupted", summary, status.UpdatedAt));
            }
            if (status.Outcome is EOutcome.Failed or EOutcome.NotExecuted)
            {
                notifications.Add(UpdateNotification.Create("update.failed", summary, status.UpdatedAt));
            }
        }
        return notifications;
    }
}
