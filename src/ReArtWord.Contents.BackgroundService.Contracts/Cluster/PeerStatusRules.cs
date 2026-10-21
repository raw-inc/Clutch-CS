namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster;

/// <summary>未同期・識別競合と、範囲を隔離済みの協調可能状態を区別します。</summary>
public static class PeerStatusRules
{
    /// <summary>用途別の依存検査を前提に参加集合へ含められる状態です。</summary>
    /// <returns>協調可能な状態か。</returns>
    public static bool CanParticipate(EPeerStatus status) => status is EPeerStatus.Ready or EPeerStatus.Degraded;
}
