namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

/// <summary>クライアント自身のOSで検出したNICです。</summary>
/// <param name="InterfaceId">再選択に用いる固定識別子。</param>
/// <param name="Name">表示名。</param>
/// <param name="Available">現在稼働中か。</param>
/// <param name="Addresses">現存するIPv4割当。</param>
public sealed record ClientNetworkInterface(string InterfaceId, string Name, bool Available, IReadOnlyList<ClientNetworkAddress> Addresses);
