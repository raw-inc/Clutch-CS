namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;

/// <summary>利用者が選択したドライバー公開モードです。</summary>
/// <param name="Mode">解像度・Hzを組として変更するモードです。</param>
public sealed record DeviceDisplayChange(DeviceDisplayMode Mode)
{
    /// <summary>任意のドライバーデータや無効な画面寸法を受け付けません。</summary>
    public void Validate()
    {
        if (Mode is null || Mode.Width is < 1 or > 32768 || Mode.Height is < 1 or > 32768
            || Mode.RefreshRate is < 2 or > 2000 || Mode.BitsPerPixel is < 8 or > 64 || 3 < Mode.Flags)
        {
            throw new ArgumentException("Device.InvalidDisplayMode");
        }
    }
}
