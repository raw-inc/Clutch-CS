using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>無操作時間・電源供給・明示設定から、再送しない一回を選びます。</summary>
internal static class IdleShutdownDecision
{
    /// <summary>未確認の入力や将来時刻ではシャットダウンを開始しません。</summary>
    /// <returns>開始対象期間の識別子。条件未達ならnullです。</returns>
    internal static string? Episode(DeviceIdleShutdownPolicy policy, bool? ac, DeviceUserActivity? activity, DateTimeOffset now)
    {
        if (ac is null || activity is null || policy.Version == Guid.Empty || now < activity.LastActivity || now < policy.EnabledAt)
        {
            return null;
        }
        uint seconds = ac.Value ? policy.AcSeconds
                                : policy.DcSeconds;
        DateTimeOffset since = activity.LastActivity < policy.EnabledAt ? policy.EnabledAt
                                                                        : activity.LastActivity;
        if (seconds == 0 || now - since < TimeSpan.FromSeconds(seconds))
        {
            return null;
        }

        // The supply does not create a new attempt during the same idle episode.
        return ContractJson.Hash(new { policy.Version, activity.Identity });
    }
}
