namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;

/// <summary>固定した物理接続と、現在の表示・対応モードです。</summary>
/// <param name="Id">接続モニターとアダプターから作る識別子です。</param>
/// <param name="DeviceName">Windowsの表示デバイス名です。</param>
/// <param name="Name">接続ディスプレイの表示名です。</param>
/// <param name="Primary">主画面であるかです。</param>
/// <param name="SessionId">照会した対話コンソールのセッションです。</param>
/// <param name="Current">現在の表示モードです。</param>
/// <param name="Modes">表示ドライバーが確認した選択候補です。</param>
/// <param name="Revision">個別変更の比較に使う内容版です。</param>
public sealed record DeviceDisplayInfo(string Id, string DeviceName, string Name, bool Primary, int SessionId,
    DeviceDisplayMode Current, IReadOnlyList<DeviceDisplayMode> Modes, string? Revision = null);
