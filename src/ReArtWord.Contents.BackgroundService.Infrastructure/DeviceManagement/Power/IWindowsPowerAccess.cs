using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>電源設定の読取りと限定した変更を検証用OSから分離します。</summary>
internal interface IWindowsPowerAccess
{
    /// <summary>現在の設定を取得します。</summary>
    /// <returns>能力と現在値です。</returns>
    public DevicePowerState Read();

    /// <summary>一つの電源プラン項目を保存します。</summary>
    public void Write(Guid scheme, bool ac, string setting, uint value);

    /// <summary>編集中のプランが現在も有効なことを確認して反映します。</summary>
    public void Activate(Guid scheme);

    /// <summary>Windowsの高速スタートアップを設定します。</summary>
    public void SetFastStartup(bool enabled);

    /// <summary>PC共通の無操作停止設定を一括保存します。</summary>
    public void SetIdleShutdown(DeviceIdleShutdownPolicy policy);
}
