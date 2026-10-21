namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>削除確認用のスイッチ、接続先、変更可否を一つの内容版へ固定します。</summary>
/// <param name="Switch">既存のスイッチ情報。</param>
/// <param name="PhysicalInterfaceIds">対応が確認できた物理NIC。</param>
/// <param name="AllowManagementOS">管理OSの共有有無。</param>
/// <param name="Ports">管理OSとゲストVMの接続。</param>
/// <param name="Editable">削除可能か。</param>
/// <param name="Problem">削除できない理由。</param>
public sealed record DeviceVirtualSwitchDetails(DeviceVirtualSwitch Switch, IReadOnlyList<string> PhysicalInterfaceIds,
    bool AllowManagementOS, IReadOnlyList<DeviceVirtualSwitchPort> Ports, bool Editable, string? Problem = null);
