using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;

/// <summary>実ドライバーとの表示列挙とモード変更の境界です。</summary>
internal interface IWindowsDisplayDriver
{
    /// <summary>対話コンソールの表示接続を列挙します。</summary>
    /// <returns>実在する表示と公開モードです。</returns>
    public IReadOnlyList<DeviceDisplayInfo> Read();

    /// <summary>OS設定を変更せず、ドライバーがモードを受け付けるか検証します。</summary>
    /// <returns>Windowsの表示変更結果コードです。</returns>
    public int Test(string device, DeviceDisplayMode mode);

    /// <summary>表示モードを適用し、Windowsへ永続保存します。</summary>
    /// <returns>Windowsの表示変更結果コードです。</returns>
    public int Apply(string device, DeviceDisplayMode mode);
}
