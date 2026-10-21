using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>別画面・別ClientHostへ渡す更新状況です。配布ファイル、配置パス、診断本文を含みません。</summary>
public sealed record FleetUpdateActivity(Guid OperationId, string ReleaseId, bool Completed, EOutcome Outcome,
    int Confirmed, int Total, int Attention, DateTimeOffset UpdatedAt, IReadOnlyList<FleetUpdateActiveTarget> Current, string? ClientState = null)
{
    /// <summary>試行や保留を成功へ数えない確認済み割合です。</summary>
    public int Percent => (int)(100L * Confirmed / Math.Max(1, Total));

    /// <summary>更新所有者が保存した進捗を、表示に必要な最小限へまとめます。</summary>
    /// <returns>CoreとClientHostを含む固定総数と実確認数です。</returns>
    public static FleetUpdateActivity From(FleetUpdateView update)
    {
        ArgumentNullException.ThrowIfNull(update);
        string complete = "Completed";
        int confirmed = update.Peers.Count(peer => string.Equals(peer.State, complete, StringComparison.Ordinal));
        int attention = update.Peers.Count(peer => peer.State is "Failed" or "Deferred" or "Unconfirmed");
        int total = update.Peers.Count;
        string? clientState = null;
        if (update.Client is { } client)
        {
            total++;
            if (client.Completed)
            {
                if (client.Outcome == EOutcome.Succeeded)
                {
                    confirmed++;
                }
                else
                {
                    attention++;
                }
            }
            if (!client.Completed && confirmed == update.Peers.Count)
            {
                clientState = client.State;
            }
        }
        return new(update.OperationId, update.ReleaseId, update.Completed, update.Outcome, confirmed, total, attention, update.UpdatedAt,
            update.Peers.Where(peer => peer.State is "Preparing" or "Updating" or "Retrying")
                .Select(peer => new FleetUpdateActiveTarget(peer.Target.ClusterId, peer.Target.Peer.PeerId, peer.Target.Peer.HostId, peer.State)).ToArray(), clientState);
    }
}

