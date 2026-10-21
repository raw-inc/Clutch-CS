using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;

/// <summary>電源操作をせずに取得したNICと、送信元候補の選択に使うIPv4ネットワークです。</summary>
/// <param name="Interfaces">物理・仮想を区別したNICです。</param>
/// <param name="Addresses">このPCで確認したIPv4アドレスです。</param>
public sealed record DeviceWakeInventory(IReadOnlyList<DeviceWakeInterface> Interfaces, IReadOnlyList<DeviceIpAddress> Addresses);
