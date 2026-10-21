using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;

namespace ReArtWord.Contents.BackgroundService.CoreService.DeviceManagement;

/// <summary>Session 0のCoreから対話コンソールの固定プロセスへ表示操作を渡します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DisplayAccess : IDeviceDisplayAccess
{
    /// <summary>対話コンソールで実際の表示候補を取得します。</summary>
    /// <returns>対象の内容版と設定です。</returns>
    public async Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default)
        => (await ConsoleSettingsAccess.ExchangeAsync(new(request), cancellationToken).ConfigureAwait(false)).Snapshot ?? throw new InvalidOperationException("Device.DisplayReadFailed");

    /// <summary>固定された接続と内容版の変更を一回だけ実行します。</summary>
    /// <returns>元要求に対応する変更結果です。</returns>
    public async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default)
        => (await ConsoleSettingsAccess.ExchangeAsync(new(request.Read, request, before), cancellationToken).ConfigureAwait(false)).Result ?? throw new InvalidOperationException("Device.ResultUnconfirmed");
}
