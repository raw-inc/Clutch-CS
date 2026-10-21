namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;

/// <summary>NICに属するアドレスとネットワーク範囲です。</summary>
/// <param name="Address">IPアドレス。</param>
/// <param name="PrefixLength">ネットワーク部のビット数。</param>
/// <param name="Automatic">DHCP等で取得したアドレスか。</param>
public sealed record DeviceIpAddress(string Address, int PrefixLength, bool Automatic = false);
