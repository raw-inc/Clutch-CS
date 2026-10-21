namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;

/// <summary>表示ドライバーが列挙した、解像度とリフレッシュレートの組です。</summary>
/// <param name="Width">水平画素数です。</param>
/// <param name="Height">垂直画素数です。</param>
/// <param name="RefreshRate">ドライバーが公開するHzです。</param>
/// <param name="BitsPerPixel">モードの色深度です。</param>
/// <param name="Flags">インターレース等を区別する表示フラグです。</param>
public sealed record DeviceDisplayMode(int Width, int Height, int RefreshRate, int BitsPerPixel, uint Flags);
