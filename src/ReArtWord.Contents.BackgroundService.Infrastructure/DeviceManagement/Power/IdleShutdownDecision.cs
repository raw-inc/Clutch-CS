using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>無操作時間・電源供給・明示設定から、再送しない一回を選びます。</summary>
internal static class IdleShutdownDecision
{
    /// <summary>未確認の入力や将来時刻ではシャットダウンを開始しません。</summary>
    /// <returns>開始対象期間の識別子。条件未達ならnullです。</returns>
    internal static string? Episode(DeviceIdleShutdownPolicy policy, bool? ac, DeviceUserActivity? activity, TimeSpan enabled, TimeSpan idle)
    {
        if (ac is null || activity is null || policy.Version == Guid.Empty)
        {
            return null;
        }
        uint seconds = ac.Value ? policy.AcSeconds
                                : policy.DcSeconds;
        TimeSpan wait = TimeSpan.FromSeconds(seconds);
        if (seconds == 0 || enabled < wait || idle < wait)
        {
            return null;
        }

        // 電源供給が変わっても、同じ無操作期間の停止要求は再送しません。
        return ContractJson.Hash(new { policy.Version, activity.Identity });
    }
}
