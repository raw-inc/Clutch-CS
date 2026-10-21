using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WindowsUpdate;

/// <summary>Windowsの更新管理と試験用OS境界を分離します。</summary>
internal interface IWindowsUpdateAccess
{
    /// <summary>現在の設定と外部管理を確認します。</summary>
    /// <returns>読取りだけで取得した現在値です。</returns>
    public DeviceWindowsUpdateState Read();

    /// <summary>ローカルポリシーに保存し、有効な設定へ反映します。</summary>
    /// <returns>明示した変更の一回の実行です。</returns>
    public Task WriteAsync(DeviceWindowsUpdateChange change, Action<string> completed, CancellationToken cancellationToken = default);
}
