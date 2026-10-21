using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

namespace ReArtWord.Contents.BackgroundService.CoreService.DeviceManagement;

/// <summary>サービスからもコンソール内の入力時刻を取得し、他のログオンセッションと照合します。</summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleInputAccess
{
    /// <summary>対話コンソールの実入力とWTSの全セッションを確認します。</summary>
    /// <returns>確認できないセッションがあればnullです。</returns>
    internal static async Task<DeviceUserActivity?> ReadAsync(CancellationToken cancellationToken = default)
    {
        DeviceUserActivity? local = WindowsIdleInput.CurrentSession();
        if (local is null)
        {
            ConsoleSettingsWorker.Reply reply = await ConsoleSettingsAccess.ExchangeAsync(new(new(EDeviceArea.Power), ReadInput: true), cancellationToken).ConfigureAwait(false);
            local = reply.Activity;
        }
        return WindowsIdleInput.LastActivity(local);
    }
}
