namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>スイッチ削除の影響確認に使う接続済みアダプターです。</summary>
/// <param name="Id">接続アダプターの識別子。</param>
/// <param name="Name">アダプター名。</param>
/// <param name="ManagementOS">管理OS用か。</param>
/// <param name="VirtualMachine">ゲストVMの表示名。</param>
public sealed record DeviceVirtualSwitchPort(string Id, string Name, bool ManagementOS, string? VirtualMachine = null);
