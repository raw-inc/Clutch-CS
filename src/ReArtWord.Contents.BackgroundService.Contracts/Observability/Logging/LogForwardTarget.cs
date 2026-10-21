namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>ログTCPの明示転送先です。</summary>
/// <param name="PeerId">PeerIdの指定値。</param>
/// <param name="RemoteAddress">RemoteAddressの指定値。</param>
/// <param name="Port">Portの指定値。</param>
public sealed record LogForwardTarget(string PeerId, string RemoteAddress, int Port);
