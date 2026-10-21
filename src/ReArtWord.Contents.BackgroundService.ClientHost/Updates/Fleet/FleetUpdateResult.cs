using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>明示的な復元と、更新失敗後に確認された旧版復帰を同じ境界で判定します。</summary>
internal static class FleetUpdateResult
{
    /// <summary>Updaterが旧版の別実体への復帰を確認した結果だけを受け入れます。</summary>
    /// <returns>保留解除と稼働確認へ進める復元結果ならtrueです。</returns>
    internal static bool IsRestored(UpdatePeerResult result, Guid previousInstance)
    {
        if (result.InstanceId is not { } instance || instance == Guid.Empty || instance == previousInstance)
        {
            return false;
        }
        if (result.Phase == EUpdatePhase.RolledBack && result.Outcome is EOutcome.Succeeded or EOutcome.Failed)
        {
            return true;
        }
        string restored = "update.failedOldReleaseRestored";
        return result.Phase == EUpdatePhase.Failed && result.Outcome == EOutcome.Failed
            && string.Equals(result.Reason, restored, StringComparison.Ordinal);
    }
}
