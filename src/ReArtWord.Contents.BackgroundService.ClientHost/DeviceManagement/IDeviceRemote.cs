using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>固定したPeer実体へのOS操作と結果照会の境界です。</summary>
internal interface IDeviceRemote
{
    /// <summary>変更前の設定を読みます。</summary>
    /// <returns>対象実体の設定版です。</returns>
    public Task<DeviceSnapshot> ReadAsync(DeviceTargetRead request, CancellationToken cancellationToken = default);

    /// <summary>固定した変更を一度だけ送ります。</summary>
    /// <returns>確認済み結果または不明です。</returns>
    public Task<DeviceChangeResult> ApplyAsync(DevicePlannedChange change, CancellationToken cancellationToken = default);

    /// <summary>副作用を伴わず元操作を照会します。</summary>
    /// <returns>保存された結果または照会不能です。</returns>
    public Task<DeviceChangeResult?> ResultAsync(DevicePlannedChange change, CancellationToken cancellationToken = default);
}
