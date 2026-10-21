namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

/// <summary>クライアント自身のOSで選択したNICと到達範囲です。</summary>
/// <param name="InterfaceId">固定したNIC識別子。</param>
/// <param name="BindAddress">明示local IPv4。</param>
/// <param name="BroadcastAddress">明示ブロードキャスト先。</param>
/// <param name="Port">Coreと共有するUDPポート。</param>
public sealed record ClientNetworkBinding(string InterfaceId, string BindAddress, string BroadcastAddress, int Port);
