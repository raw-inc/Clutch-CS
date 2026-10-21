namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>接続開始時に確定した物理画面の座標と表示寸法です。</summary>
/// <param name="Id">セッション内の画面識別子。</param>
/// <param name="Name">OSが返した画面名。</param>
/// <param name="Left">仮想デスクトップ内の左端。</param>
/// <param name="Top">仮想デスクトップ内の上端。</param>
/// <param name="Width">物理ピクセル単位の幅。</param>
/// <param name="Height">物理ピクセル単位の高さ。</param>
/// <param name="Primary">主画面か。</param>
public sealed record DesktopMonitor(int Id, string Name, int Left, int Top, int Width, int Height, bool Primary)
{
    /// <summary>ホストのRaw Inputマウスを確認できた場合だけ真偽を設定します。未対応・確認不能はnullです。</summary>
    public bool? MousePresent
    {
        get; init;
    }
}
