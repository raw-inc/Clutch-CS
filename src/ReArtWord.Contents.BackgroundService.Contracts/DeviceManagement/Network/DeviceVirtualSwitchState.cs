namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>既存NIC一覧の契約を変えず、スイッチ管理時だけ取得する構成です。</summary>
/// <param name="Switches">影響確認を含む既存スイッチ。</param>
/// <param name="PhysicalInterfaces">External作成用の物理NIC候補。</param>
public sealed record DeviceVirtualSwitchState(IReadOnlyList<DeviceVirtualSwitchDetails> Switches, IReadOnlyList<DeviceVirtualSwitchAdapter> PhysicalInterfaces);
