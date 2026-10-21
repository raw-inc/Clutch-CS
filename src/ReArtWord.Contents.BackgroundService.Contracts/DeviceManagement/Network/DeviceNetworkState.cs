namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>実NICと既存Hyper-V構成の一覧です。</summary>
/// <param name="Interfaces">実在するNIC。</param>
/// <param name="Switches">取得できた仮想スイッチ。</param>
/// <param name="VirtualNics">管理OS用vNIC。</param>
/// <param name="HyperVProblem">Hyper-Vの取得不能理由。</param>
public sealed record DeviceNetworkState(IReadOnlyList<DeviceNetworkInterface> Interfaces, IReadOnlyList<DeviceVirtualSwitch> Switches, IReadOnlyList<DeviceVirtualNic> VirtualNics, string? HyperVProblem = null);
