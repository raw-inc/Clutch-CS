using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;

/// <summary>Updaterの結果から、旧版への復帰を確認できたか判定します。</summary>
internal static class FleetUpdateResult
{
    /// <summary>旧版への復帰結果と、再起動によるInstanceIdの変更を確認します。</summary>
    /// <returns>旧版への復帰を確認できた場合はtrue。</returns>
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
