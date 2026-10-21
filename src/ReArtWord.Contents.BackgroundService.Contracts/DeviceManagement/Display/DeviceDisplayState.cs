namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;

/// <summary>実際に取得した接続ディスプレイの一覧です。</summary>
/// <param name="Displays">表示モードを変更できるアクティブな表示接続です。</param>
public sealed record DeviceDisplayState(IReadOnlyList<DeviceDisplayInfo> Displays);
