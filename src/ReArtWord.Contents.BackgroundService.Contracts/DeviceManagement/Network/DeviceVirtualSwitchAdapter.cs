namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>Externalスイッチへ接続できる物理NICの実状態です。</summary>
/// <param name="Id">InterfaceGuid。</param>
/// <param name="Name">表示名。</param>
/// <param name="Description">OSへ渡す一意なデバイス説明。</param>
/// <param name="InUse">既存スイッチの使用中、未接続デバイス、またはOSへ一意に指定できない候補か。</param>
public sealed record DeviceVirtualSwitchAdapter(string Id, string Name, string Description, bool InUse);
