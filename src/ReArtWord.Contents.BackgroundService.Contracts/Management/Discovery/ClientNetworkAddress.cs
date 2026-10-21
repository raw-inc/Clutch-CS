namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

/// <summary>クライアント上のNICに現存するIPv4割当です。</summary>
/// <param name="Address">IPv4アドレス。</param>
/// <param name="PrefixLength">ネットワークprefix長。</param>
/// <param name="BroadcastAddress">この割当から得たブロードキャスト候補。</param>
public sealed record ClientNetworkAddress(string Address, int PrefixLength, string BroadcastAddress);
