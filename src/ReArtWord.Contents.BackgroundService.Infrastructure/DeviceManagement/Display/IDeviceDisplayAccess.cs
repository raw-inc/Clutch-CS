using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;

/// <summary>対話コンソール内で表示設定を読み取り、固定したモードだけを変更します。</summary>
public interface IDeviceDisplayAccess
{
    /// <summary>表示一覧または一つの表示接続を取得します。</summary>
    /// <returns>変更比較に使う内容版と実設定です。</returns>
    public Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>開始直前に再照合し、対応モードを一回だけ適用します。</summary>
    /// <returns>OSが確認した成否です。</returns>
    public Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default);
}
