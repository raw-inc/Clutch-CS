using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>固定されたOS操作と管理受付の境界です。</summary>
internal interface IDeviceSettingsPlatform
{
    /// <summary>OS・権限・明示ユーザーを観測します。</summary>
    /// <returns>現在利用できる設定領域です。</returns>
    internal DeviceCapabilities Capabilities();

    /// <summary>このPC上の限定した設定だけを読みます。</summary>
    /// <returns>取得した値と内容版です。</returns>
    internal Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>確認済み設定が変わっていないことを再確認して明示変更を行います。</summary>
    /// <returns>実際に確認できた段階と成否です。</returns>
    internal Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default);
}
